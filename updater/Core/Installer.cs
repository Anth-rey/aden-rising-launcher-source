using System.Text.Json;

namespace AdenRising.Updater.Core;

public sealed record PlannedFile(ManifestEntry Entry, string Reason);

public sealed record InstallPlan(
    Manifest Manifest,
    IReadOnlyList<PlannedFile> Work,
    IReadOnlyList<string> Remove,
    int UpToDate)
{
    public long Bytes => Work.Sum(w => w.Entry.Size);
    public bool NothingToDo => Work.Count == 0 && Remove.Count == 0;

    /// <summary>Blobs are moved into place rather than copied, so the extra room
    /// needed on top of the finished game is one file, not a second copy of
    /// everything.</summary>
    public long PeakExtraBytes => Work.Count == 0 ? 0 : Work.Max(w => w.Entry.Size);
}

public sealed class Installer(string gameDir, BlobStore blobs)
{
    private string StateDir => Path.Combine(gameDir, ".updater");
    private string InstalledManifestPath => Path.Combine(StateDir, "installed-manifest.json");
    private string NoTranslationPath => Path.Combine(StateDir, "no-d3d9-translation");

    /// <summary>
    /// The graphics wrapper: a Direct3D 9 that the client talks to instead of
    /// the one in Windows, which then draws through Direct3D 11. It is what
    /// gives the game working full screen, vertical sync, anti-aliasing and a
    /// mouse that stays inside the window -- none of which the client can do
    /// for itself.
    ///
    /// On a machine where it cannot start, it takes the game down with it
    /// before there is a window to put an error in, which is why the game just
    /// vanishes. Hence the ability to refuse it below.
    /// </summary>
    private static readonly string[] Translation = ["system/d3d9.dll", "system/dgVoodoo.conf"];

    /// <summary>
    /// Whether this machine has already proved it cannot use them. Set by the
    /// launcher after a game that died on startup, and honoured here so the
    /// next check does not simply download them again -- which is what would
    /// happen otherwise, once per launch, for ever.
    /// </summary>
    public bool TranslationRefused => File.Exists(NoTranslationPath);

    public void RefuseTranslation()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(NoTranslationPath,
                $"{DateTime.Now:u}{Environment.NewLine}The game would not start with the graphics wrapper in place.{Environment.NewLine}");
            foreach (var path in Translation)
            {
                var full = Path.Combine(gameDir, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) File.Delete(full);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private bool Skip(ManifestEntry entry) =>
        TranslationRefused && Translation.Contains(entry.Path, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Works out what has to happen. Without <paramref name="verifyEverything"/>
    /// this trusts the record of what was installed last time and only looks at
    /// what changed between versions -- hashing six gigabytes on every launch
    /// would make starting the game a chore. "Verify files" is the deliberate
    /// full pass.
    /// </summary>
    public async Task<InstallPlan> PlanAsync(
        Manifest wanted, bool verifyEverything, IProgress<int>? checkedFiles = null, CancellationToken ct = default)
    {
        var installed = ReadInstalledManifest();
        var previous = installed?.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        var work = new List<PlannedFile>();
        var upToDate = 0;
        var done = 0;

        foreach (var entry in wanted.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (Skip(entry)) { checkedFiles?.Report(++done); continue; }

            var full = Path.Combine(gameDir, entry.LocalPath);
            var exists = File.Exists(full);

            // Settings files are the player's once they exist -- but only once
            // they have actually made them theirs.
            //
            // Written once and never touched again was too blunt. A change to a
            // seeded default then reached nobody who already had the game, and
            // there was no way to give it to them short of making the file
            // managed, which would wipe everyone's keybinds on every patch.
            //
            // So: if the file on disk is still byte for byte the one we wrote,
            // the player has not customised anything and there is nothing to
            // protect -- take the new one. The moment they change a single
            // key it stops matching and we never touch it again.
            //
            // This rests on the client not rewriting these files behind us,
            // which was checked rather than assumed: three installs, two of
            // them played all day, all three still hashed to exactly what the
            // launcher had put there.
            if (entry.Kind == FileKind.Seed)
            {
                if (!exists)
                {
                    work.Add(new(entry, "missing"));
                }
                else if (previous is not null
                         && previous.TryGetValue(entry.Path, out var seeded)
                         && !seeded.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    // Cheap: these are settings files, a few kilobytes each.
                    var actual = await BlobStore.HashFileAsync(full, ct);
                    if (actual.Equals(seeded.Sha256, StringComparison.OrdinalIgnoreCase))
                        work.Add(new(entry, "still exactly as we wrote it"));
                    else
                        upToDate++;
                }
                else
                {
                    upToDate++;
                }
                checkedFiles?.Report(++done);
                continue;
            }

            if (!exists)
            {
                work.Add(new(entry, "missing"));
            }
            else if (verifyEverything)
            {
                var actual = await BlobStore.HashFileAsync(full, ct);
                if (actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) upToDate++;
                else work.Add(new(entry, "does not match"));
            }
            else if (previous is not null && previous.TryGetValue(entry.Path, out var before)
                     && before.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // Unchanged since the version we installed, and we verified it
                // then. A size check is cheap insurance against obvious damage.
                if (new FileInfo(full).Length == entry.Size) upToDate++;
                else work.Add(new(entry, "wrong size"));
            }
            else if (previous is null)
            {
                // A file already sitting here with no record of us putting it
                // there is almost always an install that was paused or cut off
                // part way. Hashing it is what makes resuming work at all:
                // finished files are moved out of the cache onto their real
                // path, so without this the next run cannot tell they are done
                // and fetches every one of them again.
                var actual = await BlobStore.HashFileAsync(full, ct);
                if (actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)) upToDate++;
                else work.Add(new(entry, "does not match"));
            }
            else
            {
                work.Add(new(entry, "changed in this version"));
            }

            checkedFiles?.Report(++done);
        }

        return new InstallPlan(wanted, work, PlanRemovals(installed, wanted), upToDate);
    }

    /// <summary>
    /// Files a previous version put here that the new one no longer ships.
    ///
    /// Only ever files listed in the manifest we ourselves installed, which is
    /// what keeps this safe: screenshots, logs, shader caches and anything else
    /// the player or the game made were never in a manifest, so they are never
    /// candidates. Without this a file could only ever be added to a player's
    /// disk and never taken away -- so backing out a bad release would leave
    /// whatever it added behind for good.
    /// </summary>
    private List<string> PlanRemovals(Manifest? installed, Manifest wanted)
    {
        var removals = new List<string>();
        if (installed is null) return removals;

        var stillWanted = wanted.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var was in installed.Files)
        {
            if (stillWanted.Contains(was.Path)) continue;

            // Settings we seeded became the player's the moment they were
            // written. Dropping one from the manifest is not permission to
            // throw away their resolution and keybinds.
            if (was.Kind == FileKind.Seed) continue;

            if (File.Exists(Path.Combine(gameDir, was.LocalPath))) removals.Add(was.LocalPath);
        }
        return removals;
    }

    /// <summary>How many files are fetched at once. One at a time spends most of
    /// the install waiting for the next response rather than moving data: on a
    /// fast line that measured 2 MB/s against 50 MB/s for the same connection.</summary>
    private const int Parallelism = 8;

    public async Task ApplyAsync(
        InstallPlan plan, Action<long>? onBytes = null, Action<int>? onFile = null, CancellationToken ct = default)
    {
        var written = 0;

        // One job per distinct content, carrying every path that content belongs
        // at. Grouping first is what makes running them in parallel safe: no two
        // jobs can want the same blob, so none can race for it.
        var jobs = plan.Work
            .GroupBy(w => w.Entry.Sha256, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Sha256: g.Key, First: g.First().Entry, All: g.Select(w => w.Entry).ToArray()))
            .ToArray();

        var next = -1;

        async Task Worker()
        {
            while (true)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= jobs.Length) return;
                ct.ThrowIfCancellationRequested();

                var (sha256, first, all) = jobs[i];
                var target = Path.Combine(gameDir, first.LocalPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                var blob = await blobs.EnsureAsync(sha256, first.Size, onBytes, ct);
                File.Move(blob, target, overwrite: true);
                onFile?.Invoke(Interlocked.Increment(ref written));

                // Same bytes wanted at another path too: copy locally rather
                // than fetching them again.
                foreach (var also in all.Skip(1))
                {
                    var extra = Path.Combine(gameDir, also.LocalPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(extra)!);
                    File.Copy(target, extra, overwrite: true);
                    onFile?.Invoke(Interlocked.Increment(ref written));
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Min(Parallelism, Math.Max(1, jobs.Length)))
            .Select(_ => Worker()));

        // Deliberately not swallowed. A file we cannot delete is almost always
        // one the game still has open, and that is worth stopping for: the
        // record below is what remembers a removal is owed, so writing it over
        // a failed delete would lose the knowledge that the file is stale.
        foreach (var stale in plan.Remove)
        {
            ct.ThrowIfCancellationRequested();
            File.Delete(Path.Combine(gameDir, stale));
        }

        // Only once every file is in place: a record written earlier would claim
        // an install that did not finish, and the next launch would trust it.
        Directory.CreateDirectory(StateDir);
        await File.WriteAllTextAsync(
            InstalledManifestPath,
            JsonSerializer.Serialize(plan.Manifest, new JsonSerializerOptions { WriteIndented = false }),
            ct);
    }

    /// <summary>
    /// Writes the record of what is installed without moving a single file.
    ///
    /// A release can change nothing on disk and still be a new release: 1.0.3
    /// existed only to stop shipping two files. Without this the record stayed
    /// at the older version forever, and the record is what a later rollback
    /// diffs against to know which files it put there.
    /// </summary>
    public async Task RecordAsync(Manifest manifest, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StateDir);
        await File.WriteAllTextAsync(
            InstalledManifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = false }),
            ct);
    }

    public string? InstalledVersion() => ReadInstalledManifest()?.Version;

    /// <summary>
    /// The hash the record of the last install carries for a file, or null when
    /// the file is not in it. What Integrity compares the disk against before
    /// the game starts.
    /// </summary>
    public string? RecordedHash(string path) =>
        ReadInstalledManifest()?.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Sha256;

    private Manifest? ReadInstalledManifest()
    {
        if (!File.Exists(InstalledManifestPath)) return null;
        try { return Manifest.Parse(File.ReadAllText(InstalledManifestPath)); }
        catch { return null; }  // unreadable record: treat as a fresh install
    }
}
