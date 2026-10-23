using System;
using System.IO;
using NUnit.Framework;
using SwgTatooine.Server;

namespace SwgTatooine.Tests;

/// <summary>
/// Where a served run finds its asset set when <c>--assets</c> names none: beside the binary (a deployment), else up the tree in
/// <c>demo/SwgTatooine/SwgTatooine.Assets/build/assets</c> (a dev checkout), else nowhere, which refuses to serve.
/// </summary>
/// <remarks>Fake layouts in a temporary folder: a folder counts only when it holds a <c>catalogue.json</c>.</remarks>
[TestFixture]
internal sealed class AssetRootChecks
{
    private string _root;
    private string _bin;

    [SetUp]
    public void SetUp()
    {
        _root = Worlds.NewDirectory();
        _bin = Path.Combine(_root, "demo", "SwgTatooine", "SwgTatooine.Server", "bin", "Release", "net10.0");
        Directory.CreateDirectory(_bin);
    }

    [TearDown]
    public void TearDown() => Worlds.Delete(_root);

    private string RepoAssets => Path.Combine(_root, "demo", "SwgTatooine", "SwgTatooine.Assets", "build", "assets");

    private static string Publish(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, AssetOrigin.PointerName), "{}");
        return directory;
    }

    /// <summary>--assets is taken as given, even when both other places hold a set.</summary>
    [Test]
    public void AnExplicitFolderWins()
    {
        Publish(RepoAssets);
        Publish(Path.Combine(_bin, "assets"));
        Assert.That(TatooineSim.ServedAssetRoot("elsewhere", _bin), Is.EqualTo("elsewhere"));
    }

    /// <summary>A deployment's set, beside the binary, is preferred to a dev tree's.</summary>
    [Test]
    public void TheSetBesideTheBinaryWinsOverTheRepository()
    {
        Publish(RepoAssets);
        var beside = Publish(Path.Combine(_bin, "assets"));
        Assert.That(TatooineSim.ServedAssetRoot(null, _bin), Is.EqualTo(beside));
    }

    /// <summary>A dev build finds the fetched set by walking up from its bin folder.</summary>
    [Test]
    public void ADevBuildFindsTheRepositorySet()
    {
        var repo = Publish(RepoAssets);
        Assert.That(TatooineSim.ServedAssetRoot(null, _bin), Is.EqualTo(repo));
    }

    /// <summary>A folder without its catalogue is not a set: an empty <c>assets/</c> beside the binary does not hide the repository's.</summary>
    [Test]
    public void AFolderWithoutACatalogueIsSkipped()
    {
        Directory.CreateDirectory(Path.Combine(_bin, "assets"));
        var repo = Publish(RepoAssets);
        Assert.That(TatooineSim.ServedAssetRoot(null, _bin), Is.EqualTo(repo));
    }

    /// <summary>No set anywhere refuses, and says how to get one.</summary>
    [Test]
    public void NoSetRefusesAndNamesTheFetch()
    {
        Directory.CreateDirectory(RepoAssets);
        var ex = Assert.Throws<ArgumentException>(() => TatooineSim.ServedAssetRoot(null, _bin));
        Assert.That(ex.Message, Does.Contain("assets.py fetch published"));
    }
}
