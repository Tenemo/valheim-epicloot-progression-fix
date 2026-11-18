using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EpicLootProgressionFix
{
    // Writes the whole file beside its destination before it takes the old file's place, so an interrupted write leaves
    // the old file as it was. Flushing to disk first also survives a power loss, at the cost of waiting for the disk.
    internal static class WholeFile
    {
        internal static IEnumerable<string> CompleteLines(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int complete = Array.LastIndexOf(bytes, (byte)'\n') + 1;
            if (complete != bytes.Length)
            {
                string recovery = path + ".incomplete-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                File.WriteAllBytes(recovery, bytes);
                Replace(path, bytes.Take(complete).ToArray());
            }
            return Encoding.UTF8.GetString(bytes, 0, complete).Split('\n').Where(line => !string.IsNullOrWhiteSpace(line));
        }

        internal static void Replace(string path, byte[] bytes, bool flush = true)
        {
            string temporary = path + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flush);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }
}
