using System.Text.Json;

namespace EQD2Viewer.Fixtures
{
    /// <summary>
    /// Shared <see cref="JsonSerializerOptions"/> used by every read path that
    /// consumes the fixture JSON format. Centralised so that case-insensitivity,
    /// comment handling, and trailing-comma tolerance cannot drift between
    /// <see cref="FixtureLoader"/> and the DevRunner's own fixture reader.
    /// </summary>
    public static class FixtureJsonOptions
    {
        public static readonly JsonSerializerOptions Default = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }
}
