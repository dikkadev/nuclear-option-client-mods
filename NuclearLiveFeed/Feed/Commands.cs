using System;
using System.Collections.Generic;

namespace NuclearLiveFeed.Feed
{
    public enum TargetOp { Add, Remove, Set, Clear }

    /// <summary>
    /// Viewer to game: one text frame "targets &lt;add|remove|set|clear&gt; [site:app:entity ...]".
    /// Add and Remove change the list, Set replaces it, Clear empties it. Unknown commands are ignored.
    /// </summary>
    public readonly struct TargetCommand
    {
        public readonly TargetOp Op;
        public readonly List<EntityId> Ids;
        private TargetCommand(TargetOp op, List<EntityId> ids) { Op = op; Ids = ids; }

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
            for (int i = 2; i < words.Length && ids.Count < MaxIds; i++)
                if (EntityId.TryParse(words[i], out var id) && !ids.Contains(id)) ids.Add(id);
            if (ids.Count == 0 && op != TargetOp.Clear && op != TargetOp.Set) return false;
            command = new TargetCommand(op, ids);
            return true;
        }
    }
}
