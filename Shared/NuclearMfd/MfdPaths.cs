namespace NuclearMfd
{
    /// <summary>Validation for consumer-supplied roles and exact canvas-relative Unity paths.</summary>
    public static class MfdPaths
    {
        public static bool IsValidCustomSurface(string role, string canvasRelativePath)
        {
            if (string.IsNullOrWhiteSpace(role) || role == "main" || role == "weapons" || role == "radar") return false;
            foreach (char c in role)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') &&
                    !(c >= '0' && c <= '9') && c != '.' && c != '-' && c != '_') return false;
            if (role == "." || role == "..") return false;
            if (string.IsNullOrWhiteSpace(canvasRelativePath) || canvasRelativePath.IndexOf('\\') >= 0 ||
                canvasRelativePath.IndexOf(':') >= 0) return false;
            foreach (var segment in canvasRelativePath.Split('/'))
            {
                if (string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." ||
                    segment != segment.Trim()) return false;
                foreach (char c in segment) if (char.IsControl(c)) return false;
            }
            return true;
        }
    }
}
