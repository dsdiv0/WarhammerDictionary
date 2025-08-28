using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Net.Http;
using HtmlAgilityPack;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;

class WarhammerScraper
{
  // --- Configuration ---
  // Here you can easily tweak how the scraper behaves.
  static readonly string BaseUrl = "https://wh40k.lexicanum.com";
  static readonly string OutputFile = "warhammer.tsv";
  static readonly string ProgressFile = "warhammer_progress.txt"; // This file lets us resume if the script stops.
  static readonly HttpClient client = new HttpClient();
  static readonly Regex FootnoteRegex = new(@"\[\d+\]", RegexOptions.Compiled); // Pre-compiling the regex makes it faster.

  // This is our starting list of pages to crawl for links.
  static readonly string[] IndexPages = new[]
  {
        "/wiki/Imperial_Terms",
        "/wiki/List_of_Terms",
        "/wiki/Imperial_Guard_Terms",
        "/wiki/Loyal_Space_Marine_Chapters_(List)",
        "/wiki/Pictorial_List_of_Space_Marine_Chapters_A-L",
        "/wiki/Chaos_Space_Marine_Legions_and_Warbands_(List)",
        "/wiki/List_of_Titan_Legions",
        "/wiki/Traitor_Titan_Legions",
        "/wiki/Category:Lists",
        "/wiki/Category:Languages",
        "/wiki/Eldar_Lexicon",
        "/wiki/Ork_Language",
        "/wiki/T%27au_Lexicon",
        "/wiki/List_of_sentient_species",
        "/wiki/List_of_Primarchs",
        "/wiki/List_of_Imperial_Characters",
        "/wiki/List_of_Chaos_Characters",
        "/wiki/List_of_Inquisitors",
        "/wiki/List_of_Planets",
        "/wiki/List_of_Forge_Worlds",
        "/wiki/List_of_Wars",
        "/wiki/Imperial_Wargear_(List)",
        "/wiki/Necron_Lexicon",
        "/wiki/Tyranid_Nomenclature",
        "/wiki/List_of_Daemons",
        "/wiki/Category:Imperial_Organisations",
        "/wiki/Category:Technology"
    };

  // Using HashSets is super fast for checking if we've already processed an item.
  static readonly HashSet<string> SeenTitles = new();
  static readonly HashSet<string> SeenDescriptions = new();

  // --- Scraper Settings ---
  static readonly bool IncludeUrlColumn = false; // Set to true if you want a URL column in your TSV.
  static readonly int MaxParagraphs = 3;         // How many paragraphs to grab for the description.
  static readonly int MaxRetries = 3;            // How many times to retry a failed web request.
  static readonly int ParallelLimit = 5;         // How many pages to process at the same time.
  static readonly int DelayBetweenBatches = 250; // A small pause (in ms) to be polite to the server.

  static async Task Main()
  {
    // It's good practice to set a User-Agent so the website knows who is scraping.
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WH-Glossary-Bot/1.0");
    var sb = new StringBuilder();

    // Set up the header row for our TSV file.
    sb.AppendLine(IncludeUrlColumn ? "Title\tURL\tDescription" : "Title\tDescription");

    // If we've run this before and it was interrupted, load our progress.
    if (File.Exists(ProgressFile))
    {
      Console.WriteLine("Found a progress file. Resuming from where we left off...");
      foreach (var line in File.ReadAllLines(ProgressFile))
      {
        SeenTitles.Add(line);
      }
    }

    // Kick off the scraping process for each of our starting pages.
    foreach (var page in IndexPages)
    {
      Console.WriteLine($"Scraping category: {page}");
      await ScrapeCategory(page, sb);
    }

    await File.WriteAllTextAsync(OutputFile, sb.ToString());
    Console.WriteLine($"All done! Dictionary TSV saved as {OutputFile}");
    // Clean up the progress file now that we're finished.
    if (File.Exists(ProgressFile)) File.Delete(ProgressFile);
  }

  static async Task ScrapeCategory(string categoryUrl, StringBuilder sb)
  {
    try
    {
      var html = await GetWithRetries(BaseUrl + categoryUrl);
      if (string.IsNullOrEmpty(html)) return;

      var doc = new HtmlDocument();
      doc.LoadHtml(html);

      // Find all unique links on the page that point to another wiki article.
      var links = doc.DocumentNode.SelectNodes("//a[@href]")
          ?.Select(a => a.GetAttributeValue("href", ""))
          .Where(href => href.StartsWith("/wiki/"))
          .Distinct();

      if (links == null) return;

      // We process the links in small batches so we don't hammer the server all at once.
      var batch = links.Select(link => ProcessLink(link, sb)).ToArray();
      for (int i = 0; i < batch.Length; i += ParallelLimit)
      {
        var chunk = batch.Skip(i).Take(ParallelLimit);
        await Task.WhenAll(chunk);
        // A short pause between batches is good manners and helps avoid getting blocked.
        await Task.Delay(DelayBetweenBatches);
      }
    }
    catch (Exception ex)
    {
      Console.WriteLine($"Oops, failed to scrape category {categoryUrl}: {ex.Message}");
    }
  }

  static async Task ProcessLink(string link, StringBuilder sb)
  {
    // A little bit of filtering to avoid pages we don't want.
    if (link.Contains(":")) return; // Skips categories, templates, etc.
    if (link.EndsWith("(List)")) return;
    if (link.EndsWith("(Novel)")) return;
    if (link.EndsWith("(Rulebook)")) return;

    // Turn the URL part into a nice, readable title.
    var title = Uri.UnescapeDataString(link.Replace("/wiki/", "").Replace("_", " "));

    if (SeenTitles.Contains(title)) return; // We've already got this one, skip it.

    // Disambiguation pages are special; they are just lists of links to other pages.
    if (title.EndsWith("(disambiguation)"))
    {
      await ScrapeDisambiguation(link, sb);
      return;
    }

    var description = await FetchDescription(link);
    if (string.IsNullOrWhiteSpace(description)) return;

    // Sometimes different titles lead to the same description (redirects). Let's skip those.
    if (SeenDescriptions.Contains(description)) return;

    // If we've made it this far, it's a good entry. Let's add it.
    SeenTitles.Add(title);
    SeenDescriptions.Add(description);

    if (IncludeUrlColumn)
      sb.AppendLine($"{title}\t{BaseUrl + link}\t{description}");
    else
      sb.AppendLine($"{title}\t{description}");

    Console.WriteLine($"Added: {title}");

    // Save our progress immediately so we can resume if something goes wrong.
    await File.AppendAllTextAsync(ProgressFile, title + Environment.NewLine);
  }

  static async Task ScrapeDisambiguation(string disambLink, StringBuilder sb)
  {
    var html = await GetWithRetries(BaseUrl + disambLink);
    if (string.IsNullOrEmpty(html)) return;

    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    // On these pages, the real links are usually in a list.
    var innerLinks = doc.DocumentNode.SelectNodes("//div[@id='mw-content-text']//li/a[@href]")
        ?.Select(a => a.GetAttributeValue("href", ""))
        .Where(href => href.StartsWith("/wiki/") && !href.Contains(":"))
        .Distinct();

    if (innerLinks == null) return;

    // Process each link found on the disambiguation page.
    foreach (var link in innerLinks)
    {
      await ProcessLink(link, sb);
    }
  }

  // This is where the magic happens for cleaning up the article text.
  static async Task<string> FetchDescription(string link)
  {
    string html = await GetWithRetries(BaseUrl + link);
    if (string.IsNullOrEmpty(html)) return "";

    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    // First, we grab the main content area of the page.
    var contentNode = doc.DocumentNode.SelectSingleNode("//div[@id='mw-content-text']");
    if (contentNode == null) return "";

    // --- Content Cleanup ---
    // Before we grab the text, we remove all the stuff we don't want.
    // This gets rid of warning boxes like the "citation needed" one.
    contentNode.SelectNodes(".//table[contains(@class, 'metadata')]")?.ToList().ForEach(n => n.Remove());
    // This removes the big quote blocks.
    contentNode.SelectNodes(".//blockquote")?.ToList().ForEach(n => n.Remove());
    // And this removes the side info boxes.
    contentNode.SelectNodes(".//div[contains(@class, 'infobox')]")?.ToList().ForEach(n => n.Remove());

    // NOW we can safely grab the paragraphs from our cleaned-up content.
    var paragraphs = contentNode.SelectNodes(".//p[normalize-space()]");
    if (paragraphs == null) return "";

    var selectedParagraphs = paragraphs.Take(MaxParagraphs).Select(p => p.InnerText);
    var text = string.Join(" ", selectedParagraphs);

    // Final cleanup of the text itself.
    text = FootnoteRegex.Replace(text, ""); // Remove footnote markers like [1], [2], etc.
    text = System.Net.WebUtility.HtmlDecode(text); // Turn things like &amp; into &
    text = Regex.Replace(text, @"\s+", " ").Trim(); // Condense all whitespace into single spaces.

    return text;
  }

  // A simple but effective way to handle network hiccups.
  static async Task<string> GetWithRetries(string url)
  {
    for (int i = 0; i < MaxRetries; i++)
    {
      try
      {
        // Try to get the page content.
        return await client.GetStringAsync(url);
      }
      catch (HttpRequestException ex)
      {
        // If it fails, wait a moment and then the loop will try again.
        Console.WriteLine($"Request for {url} failed: {ex.StatusCode}. Retrying...");
        await Task.Delay(500 * (i + 1)); // Wait a bit longer after each failure.
      }
    }
    // If it fails after all retries, we log it and move on.
    Console.WriteLine($"Failed to fetch {url} after {MaxRetries} retries.");
    return "";
  }
}
