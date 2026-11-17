using System.IO;

namespace EpicLootProgressionFix
{
    // Writes the whole file beside its destination before it takes the old file's place, so an interrupted write leaves
    // the old file as it was. Flushing to disk first also survives a power loss, at the cost of waiting for the disk.
    internal static class WholeFile
    {
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
