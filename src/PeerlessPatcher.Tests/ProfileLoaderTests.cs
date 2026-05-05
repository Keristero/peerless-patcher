using Microsoft.Extensions.Logging.Abstractions;
using PeerlessPatcher.Profiles;

namespace PeerlessPatcher.Tests;

public class ProfileLoaderTests
{
    private readonly ProfileLoader _loader = new(NullLogger<ProfileLoader>.Instance);

    [Fact]
    public void LoadAll_ValidProfile_LoadsCorrectly()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir, "test.json"), """
            {
              "gameId": "test-game",
              "gameName": "Test Game",
              "steamAppId": 12345,
              "processName": "TestGame",
              "patches": [
                {
                  "type": "hex-edit",
                  "name": "Test Patch",
                  "description": "A test patch",
                  "offset": 1000,
                  "findBytes": [1, 2, 3],
                  "replaceBytes": [4, 5, 6]
                }
              ]
            }
            """);

        var profiles = _loader.LoadAll(dir);

        Assert.Single(profiles);
        Assert.Equal("test-game", profiles[0].GameId);
        Assert.Equal("Test Game", profiles[0].GameName);
        Assert.Equal("TestGame", profiles[0].ProcessName);
        Assert.Single(profiles[0].Patches);
        Assert.Equal("Test Patch", profiles[0].Patches[0].Name);
    }

    [Fact]
    public void LoadAll_MissingRequiredField_SkipsProfile()
    {
        using var dir = TempDir.Create();
        // Missing processName
        File.WriteAllText(Path.Combine(dir, "bad.json"), """
            {
              "gameId": "test-game",
              "gameName": "Test Game",
              "steamAppId": 12345,
              "patches": []
            }
            """);

        var profiles = _loader.LoadAll(dir);

        Assert.Empty(profiles);
    }

    [Fact]
    public void LoadAll_InvalidJson_SkipsProfile()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir, "corrupt.json"), "{ this is not valid json !!!");

        var profiles = _loader.LoadAll(dir);

        Assert.Empty(profiles);
    }

    [Fact]
    public void LoadAll_MismatchedFindReplaceBytes_SkipsPatch()
    {
        using var dir = TempDir.Create();
        File.WriteAllText(Path.Combine(dir, "mismatched.json"), """
            {
              "gameId": "g",
              "gameName": "G",
              "steamAppId": 1,
              "processName": "G",
              "patches": [
                {
                  "type": "hex-edit",
                  "name": "Bad Patch",
                  "description": "",
                  "offset": 0,
                  "findBytes": [1, 2],
                  "replaceBytes": [3, 4, 5]
                }
              ]
            }
            """);

        var profiles = _loader.LoadAll(dir);

        // Profile loads but the bad patch is excluded
        Assert.Single(profiles);
        Assert.Empty(profiles[0].Patches);
    }

    [Fact]
    public void LoadAll_MissingDirectory_ReturnsEmpty()
    {
        var profiles = _loader.LoadAll("/this/path/does/not/exist/xyz");
        Assert.Empty(profiles);
    }
}
