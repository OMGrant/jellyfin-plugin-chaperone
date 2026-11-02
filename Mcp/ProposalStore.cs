using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.Chaperone.Mcp
{
    /// <summary>
    /// A rating suggested by an MCP client (Claude, a local model…), with its evidence and what became of it.
    /// </summary>
    public sealed class Proposal
    {
        /// <summary>Gets or sets the proposal id.</summary>
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>Gets or sets the Jellyfin item id (N format).</summary>
        public string ItemId { get; set; } = string.Empty;

        /// <summary>Gets or sets the item's name when proposed, for the review list.</summary>
        public string ItemName { get; set; } = string.Empty;

        /// <summary>Gets or sets the item kind (Audio, MusicAlbum, Movie, Series).</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Gets or sets the item's rating when proposed.</summary>
        public string? PreviousRating { get; set; }

        /// <summary>Gets or sets the proposed rating.</summary>
        public string Rating { get; set; } = string.Empty;

        /// <summary>Gets or sets the client's one-paragraph reason.</summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>Gets or sets the evidence URL, if any.</summary>
        public string? EvidenceUrl { get; set; }

        /// <summary>Gets or sets the quote the client says appears at the evidence URL.</summary>
        public string? EvidenceQuote { get; set; }

        /// <summary>Gets or sets whether Chaperone found the quote at the URL: "verified", "not found", "fetch failed", "none".</summary>
        public string EvidenceCheck { get; set; } = "none";

        /// <summary>Gets or sets the proposing client (the Jellyfin API key's name).</summary>
        public string Client { get; set; } = string.Empty;

        /// <summary>Gets or sets the status: pending, applied, rejected, superseded.</summary>
        public string Status { get; set; } = "pending";

        /// <summary>Gets or sets who decided (client name for auto-applied, "admin" for reviewed).</summary>
        public string? DecidedBy { get; set; }

        /// <summary>Gets or sets when it was proposed (unix seconds).</summary>
        public long ProposedAt { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>Gets or sets when it was decided (unix seconds).</summary>
        public long? DecidedAt { get; set; }
    }

    /// <summary>
    /// Persists proposals as one JSON file in the plugin's data folder. Small (hundreds of entries at most),
    /// so the whole file is read and rewritten under a lock.
    /// </summary>
    public static class ProposalStore
    {
        private static readonly object Gate = new object();
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };

        private static string FilePath
        {
            get
            {
                var dir = Plugin.Instance?.DataFolderPath ?? Path.GetTempPath();
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "proposals.json");
            }
        }

        /// <summary>Returns all proposals, newest first.</summary>
        public static List<Proposal> All()
        {
            lock (Gate)
            {
                return Load().OrderByDescending(p => p.ProposedAt).ToList();
            }
        }

        /// <summary>Adds a proposal; any older pending proposal for the same item is marked superseded.</summary>
        public static void Add(Proposal proposal)
        {
            lock (Gate)
            {
                var all = Load();
                foreach (var old in all.Where(p => p.ItemId == proposal.ItemId && p.Status == "pending"))
                {
                    old.Status = "superseded";
                    old.DecidedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                }

                all.Add(proposal);
                Save(all);
            }
        }

        /// <summary>Applies a change to one proposal by id; returns the updated proposal or null.</summary>
        public static Proposal? Update(string id, Action<Proposal> change)
        {
            lock (Gate)
            {
                var all = Load();
                var p = all.FirstOrDefault(x => x.Id == id);
                if (p is null)
                {
                    return null;
                }

                change(p);
                Save(all);
                return p;
            }
        }

        private static List<Proposal> Load()
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<Proposal>>(File.ReadAllText(FilePath)) ?? new List<Proposal>()
                    : new List<Proposal>();
            }
            catch (Exception)
            {
                return new List<Proposal>();
            }
        }

        private static void Save(List<Proposal> all)
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Json));
            File.Move(tmp, FilePath, true);
        }
    }
}
