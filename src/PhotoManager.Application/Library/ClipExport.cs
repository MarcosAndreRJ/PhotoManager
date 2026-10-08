using System.Globalization;
using System.Security;
using System.Text;
using PhotoManager.Domain.Photos;

namespace PhotoManager.Application.Library;

/// <summary>
/// Lista de cortes dos trechos marcados, para importar no editor:
/// EDL (CMX 3600 — DaVinci Resolve e Premiere importam) e FCPXML 1.9 (DaVinci Resolve e Final Cut Pro, com o caminho dos arquivos).
/// Os trechos entram em sequência na linha do tempo, na ordem recebida.
/// </summary>
public static class ClipExport
{
    public static string ToEdl(string title, IReadOnlyList<(ClipMarker Marker, Photo Clip)> items, double fps = 30)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TITLE: {Sanitize(title)}");
        sb.AppendLine("FCM: NON-DROP FRAME");
        sb.AppendLine();
        var record = 3600.0;                                                        // a linha do tempo começa em 01:00:00:00, como nos editores
        for (var i = 0; i < items.Count; i++)
        {
            var (marker, clip) = items[i];
            var reel = $"AX{i + 1:000}";
            var duration = marker.Duration;
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1:000}  {reel,-8} V     C        {Timecode(marker.InSeconds, fps)} {Timecode(marker.OutSeconds, fps)} {Timecode(record, fps)} {Timecode(record + duration, fps)}");
            sb.AppendLine($"* FROM CLIP NAME: {clip.FileName}");
            sb.AppendLine($"* COMMENT: {Sanitize(marker.Name)}{(marker.Rating > 0 ? $" ({marker.Rating}★)" : string.Empty)}");
            sb.AppendLine($"* SOURCE FILE: {clip.CurrentPath}");
            sb.AppendLine();
            record += duration;
        }
        return sb.ToString();
    }

    public static string ToFcpxml(string title, IReadOnlyList<(ClipMarker Marker, Photo Clip)> items, int fps = 30)
    {
        string Time(double seconds) => $"{Math.Round(seconds * fps)}/{fps}s";
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE fcpxml>");
        sb.AppendLine("<fcpxml version=\"1.9\">");
        sb.AppendLine("  <resources>");
        sb.AppendLine($"    <format id=\"r0\" name=\"FFVideoFormat1080p{fps}\" frameDuration=\"1/{fps}s\" width=\"1920\" height=\"1080\"/>");
        var assets = items.Select(i => i.Clip).DistinctBy(c => c.Id).Select((clip, index) => (clip, id: $"a{index + 1}")).ToList();
        foreach (var (clip, id) in assets)
        {
            var src = new Uri(clip.CurrentPath).AbsoluteUri;
            sb.AppendLine(CultureInfo.InvariantCulture, $"    <asset id=\"{id}\" name=\"{Esc(clip.FileName)}\" start=\"0s\" duration=\"{Time(clip.DurationSeconds ?? 0)}\" hasVideo=\"1\" hasAudio=\"1\" format=\"r0\">");
            sb.AppendLine($"      <media-rep kind=\"original-media\" src=\"{Esc(src)}\"/>");
            sb.AppendLine("    </asset>");
        }
        sb.AppendLine("  </resources>");
        sb.AppendLine("  <library>");
        sb.AppendLine($"    <event name=\"{Esc(title)}\">");
        sb.AppendLine($"      <project name=\"{Esc(title)}\">");
        var total = items.Sum(i => i.Marker.Duration);
        sb.AppendLine($"        <sequence format=\"r0\" duration=\"{Time(total)}\" tcStart=\"0s\" tcFormat=\"NDF\">");
        sb.AppendLine("          <spine>");
        var offset = 0.0;
        foreach (var (marker, clip) in items)
        {
            var asset = assets.First(a => a.clip.Id == clip.Id).id;
            sb.AppendLine($"            <asset-clip ref=\"{asset}\" name=\"{Esc(marker.Name)}\" offset=\"{Time(offset)}\" start=\"{Time(marker.InSeconds)}\" duration=\"{Time(marker.Duration)}\"/>");
            offset += marker.Duration;
        }
        sb.AppendLine("          </spine>");
        sb.AppendLine("        </sequence>");
        sb.AppendLine("      </project>");
        sb.AppendLine("    </event>");
        sb.AppendLine("  </library>");
        sb.AppendLine("</fcpxml>");
        return sb.ToString();
    }

    /// <summary>HH:MM:SS:FF (não drop-frame).</summary>
    public static string Timecode(double seconds, double fps)
    {
        var totalFrames = (long)Math.Round(seconds * fps);
        var frames = totalFrames % (long)fps;
        var totalSeconds = totalFrames / (long)fps;
        return string.Create(CultureInfo.InvariantCulture, $"{totalSeconds / 3600:00}:{totalSeconds / 60 % 60:00}:{totalSeconds % 60:00}:{frames:00}");
    }

    private static string Esc(string text) => SecurityElement.Escape(text) ?? string.Empty;
    private static string Sanitize(string text) => text.Replace('\r', ' ').Replace('\n', ' ');
}
