using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace BSVRManager.Core;

/// <summary>Tiny disk cache for HTTP-sourced images (cover art).</summary>
public static class HttpCache
{
    public static string Dir
    {
        get
        {
            var d = Path.Combine(Path.GetTempPath(), "bsvr-cache");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public static string PathFor(string url)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        var sb = new StringBuilder();
        foreach (var b in hash[..16]) sb.Append(b.ToString("x2"));
        var ext = Path.GetExtension(url.Split('?')[0]);
        if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".img";
        return Path.Combine(Dir, sb + ext);
    }

    /// <summary>Downloads url to the cache (blocking) and returns the local path, or null on failure.</summary>
    public static string Fetch(string url)
    {
        var path = PathFor(url);
        if (File.Exists(path)) return path;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var bytes = http.GetByteArrayAsync(url).ConfigureAwait(false).GetAwaiter().GetResult();
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch { return null; }
    }
}
