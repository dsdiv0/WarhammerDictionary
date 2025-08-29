using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;

class WarhammerCrawler
{
  // a few settings to tweak
  static readonly string BaseUrl = "https://wh40k.lexicanum.com";
  static readonly string OutputFile = "warhammer.tsv";
  static readonly string ProgressFile = "warhammer_progress.txt"; // so we can resume if it crashes
  static readonly HttpClient client = new HttpClient();
  static readonly Regex FootnoteRegex = new(@"\[\d+\]", RegexOptions.Compiled);

  // --- More powerful filtering ---
  static readonly string[] IgnoredUrlPrefixes = { "/wiki/File:", "/wiki/Talk:", "/wiki/Help:", "/wiki/MediaWiki:", "/wiki/Special:", "/wiki/Template:", "/wiki/Lexicanum:", "/wiki/Category:Images" };
  static readonly string[] IgnoredUrlSuffixes = { "(List)", "(Novel)", "(Novella)", "(Short_Story)", "(Audio_Drama)", "(Game)", "(Rulebook)", "(Animation)", "(Disambiguation)" };
  // This list checks for keywords anywhere in the URL to catch rulebooks and specific media.
  static readonly string[] IgnoredUrlKeywords = {
        "(RPG_series)", "Rulebook", "Heresy", "Anthology", "Game_Master", "Player%27s_Guide",
        "Core_Manual", "Compendium", "Index", "Army_List", "(Audio_Book)", "Novel_Series",
        "_Journal_", "_Magazine", "Inferno", "Imperium_", "List_of_", "Known_Members_of_",
        "Known_Vessels_of_", "Tabletop", "Edition", "Main_Page"
    };


  // --- SIMPLIFIED: Using only the single best hub page to start the crawl ---
  static readonly string HubPage = "/wiki/Warhammer_40k_-_Lexicanum:List_of_Categories";

  // crawler limits and stuff
  static readonly int MaxEntries = 25000; // safety break so it doesn't run forever
  static readonly int MaxParagraphs = 3;
  static readonly int MaxRetries = 3;
  static readonly int ParallelLimit = 10;
  static readonly int DelayBetweenRequests = 100;

  // lists to keep track of things, gotta be thread-safe for parallel stuff
  static readonly ConcurrentDictionary<string, byte> QueuedOrProcessedUrls = new ConcurrentDictionary<string, byte>();
  static readonly ConcurrentQueue<string> UrlsToCrawl = new ConcurrentQueue<string>();
  static readonly ConcurrentDictionary<string, byte> SeenDescriptions = new ConcurrentDictionary<string, byte>();

  static async Task Main()
  {
    client.DefaultRequestHeaders.UserAgent.ParseAdd("WH-Glossary-Bot/3.7");

    // only write the header if the file is new
    if (!File.Exists(OutputFile))
    {
      File.WriteAllText(OutputFile, "Title\tDescription" + Environment.NewLine);
    }

    LoadProgress();

    // --- SIMPLIFIED: Directly seed from the single hub page ---
    Console.WriteLine("Finding starting links from the main hub page...");
    await SeedFromHubPage(BaseUrl + HubPage);
    Console.WriteLine($"Seeding done. Found {UrlsToCrawl.Count} links to start with.");

    // now go through the huge list of links we just made
    Console.WriteLine("Starting the main crawl...");
    var processedCount = QueuedOrProcessedUrls.Count - UrlsToCrawl.Count;
    while (UrlsToCrawl.Count > 0 && processedCount < MaxEntries)
    {
      var tasks = new List<Task>();
      for (int i = 0; i < ParallelLimit && UrlsToCrawl.Count > 0; i++)
      {
        if (UrlsToCrawl.TryDequeue(out var urlToProcess))
        {
          tasks.Add(ProcessUrl(urlToProcess));
        }
      }
      await Task.WhenAll(tasks);
      processedCount = QueuedOrProcessedUrls.Count - UrlsToCrawl.Count;
      Console.WriteLine($"To-do: {UrlsToCrawl.Count} | Done: {processedCount}/{MaxEntries}");
    }

    Console.WriteLine($"All done! Dictionary saved to {OutputFile}");
  }

  // just grabs all the links from a hub page to get us started
  static async Task SeedFromHubPage(string hubUrl)
  {
    Console.WriteLine($"Grabbing links from: {hubUrl}");
    string html = await GetWithRetries(hubUrl);
    if (string.IsNullOrEmpty(html)) return;

    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    var linksOnPage = doc.DocumentNode.SelectNodes("//div[@id='mw-content-text']//a[@href]");
    if (linksOnPage == null) return;

    foreach (var linkNode in linksOnPage)
    {
      var href = linkNode.GetAttributeValue("href", "");
      EnqueueUrl(href);
    }
  }

  // this is the main workhorse, processes one url
  static async Task ProcessUrl(string url)
  {
    await Task.Delay(DelayBetweenRequests); // play nice with their server
    string html = await GetWithRetries(url);
    if (string.IsNullOrEmpty(html)) return;

    var doc = new HtmlDocument();
    doc.LoadHtml(html);

    // find more links on the current page to crawl later
    var linksOnPage = doc.DocumentNode.SelectNodes("//div[@id='mw-content-text']//a[@href]");
    if (linksOnPage != null)
    {
      foreach (var linkNode in linksOnPage)
      {
        var href = linkNode.GetAttributeValue("href", "");
        EnqueueUrl(href);
      }
    }

    // now, actually get the content from this page
    var titleNode = doc.DocumentNode.SelectSingleNode("//h1[@id='firstHeading']");
    if (titleNode == null) return;

    var title = System.Net.WebUtility.HtmlDecode(titleNode.InnerText.Trim());

    // Skip adding category pages and other broad, non-lore topics to the dictionary
    if (title.StartsWith("Category:") || title == "Warhammer 40,000")
    {
      return;
    }

    var description = ExtractDescription(doc);
    if (string.IsNullOrWhiteSpace(description) || !SeenDescriptions.TryAdd(description, 0))
    {
      return; // skip if no description or we've seen it before
    }

    // if we got here, it's a good entry. save it.
    await File.AppendAllTextAsync(OutputFile, $"{title}\t{description}" + Environment.NewLine);
    await File.AppendAllTextAsync(ProgressFile, url.Replace(BaseUrl, "") + Environment.NewLine);
    Console.WriteLine($"Added: {title}");
  }

  // helper to check if a link is worth adding to our to-do list
  static void EnqueueUrl(string href)
  {
    // check if the link is garbage before we even add it to the queue
    if (!href.StartsWith("/wiki/") ||
        IgnoredUrlPrefixes.Any(prefix => href.StartsWith(prefix)) ||
        IgnoredUrlSuffixes.Any(suffix => href.EndsWith(suffix)) ||
        IgnoredUrlKeywords.Any(keyword => href.Contains(keyword)))
    {
      return;
    }

    var newFullUrl = BaseUrl + href;
    if (QueuedOrProcessedUrls.TryAdd(newFullUrl, 0))
    {
      UrlsToCrawl.Enqueue(newFullUrl);
    }
  }

  // for resuming a crashed session
  static void LoadProgress()
  {
    if (!File.Exists(ProgressFile)) return;
    Console.WriteLine("Found progress file, loading stuff we've already done.");
    var seenCount = 0;
    foreach (var line in File.ReadAllLines(ProgressFile))
    {
      if (!string.IsNullOrWhiteSpace(line))
      {
        QueuedOrProcessedUrls.TryAdd(BaseUrl + line, 0);
        seenCount++;
      }
    }
    Console.WriteLine($"Loaded {seenCount} URLs from last time.");
  }

  // pulls the description text out and cleans it up
  static string ExtractDescription(HtmlDocument doc)
  {
    var contentNode = doc.DocumentNode.SelectSingleNode("//div[@id='mw-content-text']");
    if (contentNode == null) return "";

    // get rid of junk like infoboxes and warning templates
    contentNode.SelectNodes(".//table[contains(@class, 'metadata')]")?.ToList().ForEach(n => n.Remove());
    contentNode.SelectNodes(".//blockquote")?.ToList().ForEach(n => n.Remove());
    contentNode.SelectNodes(".//div[contains(@class, 'infobox')]")?.ToList().ForEach(n => n.Remove());
    contentNode.SelectNodes(".//div[@id='toc']")?.ToList().ForEach(n => n.Remove());

    var paragraphs = contentNode.SelectNodes(".//p[normalize-space()]");
    if (paragraphs == null) return "";

    var text = string.Join(" ", paragraphs.Take(MaxParagraphs).Select(p => p.InnerText));

    // Remove any hidden tab characters from the description
    text = text.Replace('\t', ' '); // Replace tabs with spaces

    // final cleanup on the text itself
    text = FootnoteRegex.Replace(text, "");
    text = System.Net.WebUtility.HtmlDecode(text);
    text = Regex.Replace(text, @"\s+", " ").Trim();
    return text;
  }

  // simple retry logic in case the network flakes out
  static async Task<string> GetWithRetries(string url)
  {
    for (int i = 0; i < MaxRetries; i++)
    {
      try { return await client.GetStringAsync(url); }
      catch { await Task.Delay(500 * (i + 1)); }
    }
    Console.WriteLine($"Couldn't get {url} after {MaxRetries} tries, skipping it.");
    return "";
  }
}
