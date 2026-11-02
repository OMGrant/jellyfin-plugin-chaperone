using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chaperone.Mcp
{
    /// <summary>
    /// Chaperone's MCP server (Streamable HTTP transport, JSON responses, stateless): POST /Chaperone/mcp.
    /// Admin-level Jellyfin API keys only. Each client should use its own key: the key's name identifies the
    /// client, and only names listed in "Trusted MCP clients" have their proposals applied without review.
    /// </summary>
    [ApiController]
    [Route("Chaperone")]
    [Authorize(Policy = "RequiresElevation")]
    public class McpController : ControllerBase
    {
        private const string ProtocolVersion = "2025-06-18";

        private readonly ChaperoneTools _tools;
        private readonly IAuthorizationContext _authContext;
        private readonly IAuthenticationManager _authManager;
        private readonly ILogger<McpController> _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpController"/> class.
        /// </summary>
        public McpController(ChaperoneTools tools, IAuthorizationContext authContext, IAuthenticationManager authManager, ILogger<McpController> logger)
        {
            _tools = tools;
            _authContext = authContext;
            _authManager = authManager;
            _logger = logger;
        }

        /// <summary>No server-initiated stream: this server only answers requests.</summary>
        /// <returns>405.</returns>
        [HttpGet("mcp")]
        public IActionResult Stream() => StatusCode(StatusCodes.Status405MethodNotAllowed);

        /// <summary>One JSON-RPC message (or a batch) from an MCP client.</summary>
        /// <param name="body">The JSON-RPC request.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The JSON-RPC response, or 202 for notifications.</returns>
        [HttpPost("mcp")]
        public async Task<IActionResult> Post([FromBody] JsonElement body, CancellationToken cancellationToken)
        {
            var client = await ClientNameAsync().ConfigureAwait(false);
            if (body.ValueKind == JsonValueKind.Array)
            {
                var results = new JsonArray();
                foreach (var msg in body.EnumerateArray())
                {
                    var r = await HandleAsync(msg, client, cancellationToken).ConfigureAwait(false);
                    if (r is not null)
                    {
                        results.Add(r);
                    }
                }

                return results.Count == 0 ? Accepted() : Json(results);
            }

            var single = await HandleAsync(body, client, cancellationToken).ConfigureAwait(false);
            return single is null ? Accepted() : Json(single);
        }

        private static ContentResult Json(JsonNode node) => new ContentResult
        {
            Content = node.ToJsonString(),
            ContentType = "application/json",
            StatusCode = 200
        };

        private async Task<string> ClientNameAsync()
        {
            var info = await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
            if (info.IsApiKey && !string.IsNullOrEmpty(info.Token))
            {
                var keys = await _authManager.GetApiKeys().ConfigureAwait(false);
                var name = keys.FirstOrDefault(k => string.Equals(k.AccessToken, info.Token, StringComparison.Ordinal))?.AppName;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name!;
                }
            }

            return info.User is not null ? "admin:" + info.User.Username : "unknown";
        }

        private static bool IsTrusted(string client)
        {
            var list = Plugin.Instance?.Configuration.TrustedMcpClients ?? string.Empty;
            return list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(n => string.Equals(n, client, StringComparison.OrdinalIgnoreCase));
        }

        private async Task<JsonObject?> HandleAsync(JsonElement msg, string client, CancellationToken cancellationToken)
        {
            var hasId = msg.TryGetProperty("id", out var idEl);
            var id = hasId ? JsonNode.Parse(idEl.GetRawText()) : null;
            var method = msg.TryGetProperty("method", out var m) ? m.GetString() : null;
            var p = msg.TryGetProperty("params", out var pe) ? pe : default;

            if (!hasId)
            {
                return null; // notification (e.g. notifications/initialized): nothing to answer
            }

            try
            {
                JsonNode result = method switch
                {
                    "initialize" => Initialize(p),
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject { ["tools"] = ToolList() },
                    "tools/call" => await CallAsync(p, client, cancellationToken).ConfigureAwait(false),
                    _ => throw new McpError(-32601, $"Method not found: {method}")
                };
                return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            }
            catch (McpError e)
            {
                return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message } };
            }
        }

        private static JsonObject Initialize(JsonElement p)
        {
            var requested = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : null;
            return new JsonObject
            {
                ["protocolVersion"] = string.IsNullOrEmpty(requested) ? ProtocolVersion : requested,
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = "chaperone", ["version"] = Plugin.Instance?.Version.ToString() ?? "?" },
                ["instructions"] = "Chaperone fills missing parental ratings in this Jellyfin library. Start with rating_scale for the rules, "
                    + "then list_unrated. Research each item with your own tools (web search, lyrics sites) plus get_item and get_lyrics, "
                    + "and propose_rating only when evidence supports it — include evidence_url and an exact evidence_quote, which "
                    + "Chaperone verifies. If unsure, propose nothing: unrated items stay hidden from restricted users."
            };
        }

        private static JsonArray ToolList()
        {
            static JsonObject Tool(string name, string desc, JsonObject props, params string[] required) => new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = props,
                    ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
                }
            };
            static JsonObject Str(string d) => new JsonObject { ["type"] = "string", ["description"] = d };
            static JsonObject Int(string d) => new JsonObject { ["type"] = "integer", ["description"] = d };

            return new JsonArray
            {
                Tool("rating_scale", "The valid ratings and Chaperone's rules (music = explicit language only; never guess). Read this first.", new JsonObject()),
                Tool(
                    "list_unrated",
                    "Items that still need a parental rating: blank, 'Unrated', or a rating Jellyfin can't recognize.",
                    new JsonObject
                    {
                        ["kind"] = Str("music | album | movie | show | all (default all)"),
                        ["limit"] = Int("Page size, 1-200 (default 50)"),
                        ["offset"] = Int("Paging offset (default 0)")
                    }),
                Tool("get_item", "Everything Jellyfin knows about one item: artist/album/duration, overview, genres, provider ids, file name, an album's tracks.", new JsonObject { ["id"] = Str("Item id from list_unrated") }, "id"),
                Tool("get_lyrics", "LRCLIB lyrics for a music track (matched on artist, title and duration), including whether it's an instrumental.", new JsonObject { ["id"] = Str("Music track id") }, "id"),
                Tool(
                    "propose_rating",
                    "Propose a rating with your reason and evidence. Trusted clients' proposals apply at once (and lock the field); others queue for the admin. Music takes only the clean or explicit value from rating_scale.",
                    new JsonObject
                    {
                        ["id"] = Str("Item id"),
                        ["rating"] = Str("A recognized rating, e.g. TV-G, TV-MA, PG-13"),
                        ["reason"] = Str("One or two sentences: what the rating is based on"),
                        ["evidence_url"] = Str("Page your evidence comes from (lyrics page, ratings database…)"),
                        ["evidence_quote"] = Str("An exact passage from that page supporting the rating; Chaperone checks it's there")
                    },
                    "id",
                    "rating",
                    "reason"),
                Tool("list_proposals", "Proposals so far, newest first.", new JsonObject { ["status"] = Str("pending | applied | rejected | superseded (default all)") })
            };
        }

        private async Task<JsonObject> CallAsync(JsonElement p, string client, CancellationToken cancellationToken)
        {
            var name = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("name", out var n) ? n.GetString() : null;
            var args = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
            string? S(string key) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            int I(string key, int dflt) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : dflt;

            try
            {
                JsonNode data = name switch
                {
                    "rating_scale" => _tools.RatingScale(),
                    "list_unrated" => _tools.ListUnrated(S("kind"), I("limit", 50), I("offset", 0)),
                    "get_item" => _tools.GetItem(S("id") ?? string.Empty),
                    "get_lyrics" => await _tools.GetLyricsAsync(S("id") ?? string.Empty, cancellationToken).ConfigureAwait(false),
                    "propose_rating" => await _tools.ProposeAsync(
                        S("id") ?? string.Empty,
                        S("rating") ?? string.Empty,
                        S("reason") ?? string.Empty,
                        S("evidence_url"),
                        S("evidence_quote"),
                        client,
                        IsTrusted(client),
                        cancellationToken).ConfigureAwait(false),
                    "list_proposals" => _tools.ListProposals(S("status")),
                    _ => throw new McpError(-32602, $"Unknown tool: {name}")
                };
                return ToolResult(data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), false);
            }
            catch (ArgumentException e)
            {
                return ToolResult(e.Message, true);
            }
            catch (Exception e) when (e is not McpError && e is not OperationCanceledException)
            {
                _logger.LogWarning(e, "Chaperone MCP: tool {Tool} failed.", name);
                return ToolResult("Tool failed: " + e.Message, true);
            }
        }

        private static JsonObject ToolResult(string text, bool isError) => new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError
        };

        private sealed class McpError : Exception
        {
            public McpError(int code, string message)
                : base(message)
            {
                Code = code;
            }

            public int Code { get; }
        }
    }

    /// <summary>
    /// Admin review of queued proposals (used by Chaperone's settings page).
    /// </summary>
    [ApiController]
    [Route("Chaperone/Proposals")]
    [Authorize(Policy = "RequiresElevation")]
    public class ProposalsController : ControllerBase
    {
        private readonly ChaperoneTools _tools;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProposalsController"/> class.
        /// </summary>
        public ProposalsController(ChaperoneTools tools)
        {
            _tools = tools;
        }

        /// <summary>All proposals, newest first.</summary>
        /// <returns>The proposals.</returns>
        [HttpGet]
        public IActionResult List() => Content(_tools.ListProposals(null).ToJsonString(), "application/json");

        /// <summary>Approve (apply) a pending proposal.</summary>
        /// <param name="id">Proposal id.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The updated proposal.</returns>
        [HttpPost("{id}/Approve")]
        public async Task<IActionResult> Approve([FromRoute] string id, CancellationToken cancellationToken)
        {
            try
            {
                var p = await _tools.ApplyAsync(id, "admin", cancellationToken).ConfigureAwait(false);
                return Ok(p);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
        }

        /// <summary>Reject a pending proposal.</summary>
        /// <param name="id">Proposal id.</param>
        /// <returns>The updated proposal.</returns>
        [HttpPost("{id}/Reject")]
        public IActionResult Reject([FromRoute] string id)
        {
            try
            {
                return Ok(_tools.Reject(id, "admin"));
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
        }
    }
}
