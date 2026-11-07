using System;
using System.Collections.Generic;

namespace NuclearLiveFeed.Feed
{
    public enum TargetOp { Add, Remove, Set, Clear }

    /// <summary>
    /// Viewer to game: one text frame "targets &lt;add|remove|set|clear&gt; [site:app:entity | nlf-b-&lt;building&gt; ...]".
    /// Add and Remove change the list, Set replaces it, Clear empties it. Unknown commands are ignored.
    /// </summary>
    public readonly struct TargetCommand
    {
        public readonly TargetOp Op;
        public readonly List<EntityId> Ids;
        /// <summary>Buildings by persistent ID, from "nlf-b-&lt;id&gt;" tokens (the building uid the feed sends).</summary>
        public readonly List<uint> Buildings;
        private TargetCommand(TargetOp op, List<EntityId> ids, List<uint> buildings) { Op = op; Ids = ids; Buildings = buildings; }

        public const int MaxIds = 128;

        public static bool TryParse(string text, out TargetCommand command)
        {
            command = default;
            var words = text.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words[0] != "targets") return false;
            TargetOp op;
            switch (words[1])
            {
                case "add": op = TargetOp.Add; break;
                case "remove": op = TargetOp.Remove; break;
                case "set": op = TargetOp.Set; break;
                case "clear": op = TargetOp.Clear; break;
                default: return false;
            }
            var ids = new List<EntityId>();
            var buildings = new List<uint>();
            for (int i = 2; i < words.Length && ids.Count + buildings.Count < MaxIds; i++)
            {
                if (EntityId.TryParse(words[i], out var id)) { if (!ids.Contains(id)) ids.Add(id); }
                else if (words[i].StartsWith("nlf-b-", StringComparison.Ordinal) && uint.TryParse(words[i].Substring(6), out var key) && key < 0x80000000u && !buildings.Contains(key)) buildings.Add(key);
            }
            if (ids.Count == 0 && buildings.Count == 0 && op != TargetOp.Clear && op != TargetOp.Set) return false;
            command = new TargetCommand(op, ids, buildings);
            return true;
        }
    }
}
