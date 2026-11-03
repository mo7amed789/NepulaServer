using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NebulaServer.Helpers;

public static class ContentFilterHelper
{
    public const string BlockedMessage = "Downloading from adult or pornographic websites is strictly prohibited.";

    private static readonly HashSet<string> BlockedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        
        "pornhub.com", "phncdn.com", "pornhubpremium.com", "redtube.com", "youporn.com", "tube8.com", "brazzers.com",
        "realitykings.com", "bangbros.com", "naughtyamerica.com", "digitalplayground.com", "twistys.com",

        // Major tube sites
        "xvideos.com", "xvideos2.com", "xvideos3.com", "xnxx.com", "xnxx2.com", "xnxx3.com",
        "xhamster.com", "xhamster2.com", "xhamster.desi",
        "spankbang.com", "beeg.com", "eporner.com", "daftsex.com", "hqporner.com", "thumbzilla.com",
        "tnaflix.com", "motherless.com", "heavy-r.com", "fuq.com", "cliphunter.com", "xtube.com",
        "youjizz.com", "vporn.com", "txxx.com", "porn300.com", "drtuber.com", "nuvid.com",
        "upornia.com", "hclips.com", "hdzog.com", "4tube.com", "porntrex.com", "pornhat.com",

        // Cam & adult subscription platforms
        "chaturbate.com", "stripchat.com", "bongacams.com", "livejasmin.com", "cam4.com",
        "camwhores.tv", "camwhores.video", "myfreecams.com", "camsoda.com", "flirt4free.com",
        "onlyfans.com", "fansly.com", "manyvids.com", "clips4sale.com", "loyalfans.com",

        // Adult community & image boards / hentai
        "erome.com", "coomer.party", "coomer.su", "kemono.party", "kemono.su",
        "rule34.xxx", "rule34.paheal.net", "rule34video.com", "rule34world.com",
        "e-hentai.org", "nhentai.net", "hentaihaven.xxx", "hanime.tv", "fakku.net",
        "gelbooru.com", "danbooru.donmai.us", "luscious.net", "hentai-foundry.com",
        "xbooru.com", "hypnohub.net", "realbooru.com",

        // Asian / JAV networks
        "javhd.com", "javlibrary.com", "avgle.com", "missav.com", "jable.tv", "7mmtv.tv"
    };

    private static readonly string[] BlockedKeywords =
    {
        "porn", "xxx", "xvideos", "xnxx", "xhamster", "redtube", "youporn",
        "spankbang", "beeg", "chaturbate", "stripchat", "bongacams", "livejasmin",
        "onlyfans", "fansly", "erome", "rule34", "nhentai", "hentai", "eporner",
        "tnaflix", "motherless", "thumbzilla", "brazzers", "camwhores", "sexvid",
        "tube8", "youjizz", "cam4"
    };

    /// <summary>
    /// Checks whether the provided URL belongs to an adult or pornographic site.
    /// </summary>
    public static bool IsAdultContentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        url = url.Trim();

        string host;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                host = uri.Host.ToLowerInvariant();
            }
            else
            {
                // Strip scheme if present
                var clean = Regex.Replace(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*://", string.Empty);
                var slashIdx = clean.IndexOfAny(new[] { '/', '?', '#' });
                host = (slashIdx > 0 ? clean[..slashIdx] : clean).ToLowerInvariant();
            }
        }
        catch
        {
            host = url.ToLowerInvariant();
        }

        // Remove standard prefixes
        if (host.StartsWith("www.")) host = host[4..];
        if (host.StartsWith("m.")) host = host[2..];

        // 1. Direct domain or subdomain check against known blocked domains
        foreach (var blocked in BlockedDomains)
        {
            if (host == blocked || host.EndsWith("." + blocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 2. Check for adult TLDs (e.g., .xxx, .porn, .adult, .sex)
        if (host.EndsWith(".xxx", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".porn", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".adult", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".sex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Check for adult keyword patterns inside the domain name parts
        var domainParts = host.Split('.');
        foreach (var part in domainParts)
        {
            foreach (var kw in BlockedKeywords)
            {
                if (part.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
