using System;
using System.IO;
using System.Linq;
using BepInEx.Configuration;

namespace EpicLootProgressionFix
{
    internal static class IntegrationChanges
    {
        internal static void Defer(Action apply, Action undo)
        {
            var patches = OptionalIntegrations.Preparing;
            if (patches == null) { apply(); return; }
            bool started = false;
            patches.Changes.Add(() => { started = true; apply(); });
            patches.Undo.Add(() => { if (started) undo(); });
        }

        internal static void ConfigValue(ConfigEntryBase entry, object value)
        {
            object original = entry.BoxedValue;
            if (Equals(original, value)) return;
            void Set(object selected)
            {
                bool save = entry.ConfigFile.SaveOnConfigSet;
                try { entry.ConfigFile.SaveOnConfigSet = false; entry.BoxedValue = selected; }
                finally { entry.ConfigFile.SaveOnConfigSet = save; }
            }
            Defer(() => Set(value), () => Set(original));
            Save(entry.ConfigFile);
        }

        internal static void Save(ConfigFile config)
        {
            var patches = OptionalIntegrations.Preparing;
            if (patches == null) { config.Save(); return; }
            string path = config.ConfigFilePath, backup = null;
            bool writing = false, backupOwned = false, hadDirectory = Directory.Exists(Path.GetDirectoryName(path));
            Defer(() =>
            {
                FileStream original = null;
                try { original = File.OpenRead(path); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                if (original != null)
                    using (original)
                    {
                        backup = path + ".initialization-" + Guid.NewGuid().ToString("N") + ".tmp";
                        using (var copy = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            backupOwned = true;
                            original.CopyTo(copy);
                        }
                    }
                // A backup failure never reaches the original writer. Rollback moves the complete backup back, so
                // restoring an existing file does not need a second allocation after a full-disk write failure.
                writing = true;
                config.Save();
            }, () =>
            {
                if (writing)
                {
                    if (backup != null)
                    {
                        if (File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(backup))) File.Delete(backup);
                        else if (File.Exists(path)) File.Replace(backup, path, null);
                        else File.Move(backup, path);
                        backupOwned = false;
                    }
                    else File.Delete(path);
                }
                if (backupOwned) File.Delete(backup);
                if (!hadDirectory && Directory.Exists(Path.GetDirectoryName(path)) && Directory.GetFileSystemEntries(Path.GetDirectoryName(path)).Length == 0)
                    Directory.Delete(Path.GetDirectoryName(path));
            });
            patches.Cleanup.Add(() => { if (backupOwned) { File.Delete(backup); backupOwned = false; } });
        }
    }
}
