using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;

namespace EpicLootProgressionFix
{
    // A joining client must run exactly the plugins in the server's client-plugins.txt, one guid|version|name line each,
    // which promoting a client import writes. Dyrr refuses plugins the pack doesn't have; this adds other versions, missing
    // plugins and plugins the pack runs only on the server. A plugin Dyrr's own list admits stays Dyrr's decision even when
    // the server runs it too, as an admin's personal plugin may. Without the file only Progression Fix's own version counts.
    internal static class ClientPlugins
    {
        internal const string Refusal = "Your mods don't match this server. Start the game with Launch modded in Gale to get the current pack.";
        private const int LongestReport = 65536;

        internal static string Report => string.Join("\n", Chainloader.PluginInfos.Values.Where(plugin => plugin.Instance != null)
            .Select(plugin => plugin.Metadata.GUID + "|" + plugin.Metadata.Version));

        // Read at every join, as Dyrr reads its list, so removing the file lets clients in without a restart.
        internal static string Difference(string report, ManualLogSource logger)
        {
            string path = Path.Combine(Paths.ConfigPath, "client-plugins.txt");
            if (File.Exists(path))
            {
                string admitted = Path.Combine(Paths.ConfigPath, "dyrr-mods.txt");
                return Compare(File.ReadAllLines(path), report, ServerOnly(Chainloader.PluginInfos.ToDictionary(plugin => plugin.Key, plugin => plugin.Value.Metadata.Name),
                    File.Exists(admitted) ? File.ReadAllLines(admitted) : new string[0]));
            }
            logger.LogWarning("This server has no client-plugins.txt, so only Progression Fix's version is compared.");
            var own = Chainloader.PluginInfos[Plugin.Id].Metadata;
            return Compare(new[] { own.GUID + "|" + own.Version + "|" + own.Name }, report, new Dictionary<string, string>());
        }

        internal static string Compare(string[] list, string report, IDictionary<string, string> serverPlugins)
        {
            if (report == null || report.Length > LongestReport) return Refusal;
            var client = new Dictionary<string, string>();
            foreach (string line in report.Split('\n'))
            {
                int split = line.LastIndexOf('|');
                if (split > 0) client[line.Substring(0, split)] = line.Substring(split + 1);
            }
            var differences = new List<string>();
            foreach (var plugin in list.Select(line => line.Split(new[] { '|' }, 3)).Where(fields => fields.Length == 3))
            {
                if (!client.TryGetValue(plugin[0], out string version)) differences.Add(plugin[2] + ": missing, this server needs " + plugin[1]);
                else if (version != plugin[1]) differences.Add(plugin[2] + ": you have " + version + ", this server needs " + plugin[1]);
                client.Remove(plugin[0]);
            }
            differences.AddRange(client.Keys.Where(serverPlugins.ContainsKey).Select(guid => serverPlugins[guid] + ": remove it, only this server runs it"));
            return differences.Count == 0 ? null : Refusal + "\n" + string.Join("\n", differences);
        }

        // The server's plugins a client must not run: all of them except those Dyrr's list admits, one GUID per line with
        // an optional # comment.
        internal static Dictionary<string, string> ServerOnly(IDictionary<string, string> serverPlugins, string[] dyrrLines)
        {
            var admitted = new HashSet<string>(dyrrLines.Select(line => line.Split('#')[0].Trim()).Where(guid => guid.Length > 0));
            return serverPlugins.Where(plugin => !admitted.Contains(plugin.Key)).ToDictionary(plugin => plugin.Key, plugin => plugin.Value);
        }
    }
}
