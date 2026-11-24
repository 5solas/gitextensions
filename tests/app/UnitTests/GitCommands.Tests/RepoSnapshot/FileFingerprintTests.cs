using GitCommands.RepoSnapshot;

namespace GitCommandsTests.RepoSnapshot;

[TestFixture]
public sealed class FileFingerprintTests
{
    private string _directory = null!;
    private string _file = null!;
    private string _missingFile = null!;

    [SetUp]
    public void SetUp()
    {
        _directory = Directory.CreateTempSubdirectory("hj-fingerprint-").FullName;
        _file = Path.Combine(_directory, "file");
        _missingFile = Path.Combine(_directory, "missing directory", "missing");
        File.WriteAllText(_file, "content");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public void IsCurrent_should_be_true_if_nothing_changed()
    {
        FileFingerprint fingerprint = Read();
        fingerprint.IsCurrent().Should().BeTrue();

        // The time does not matter
        File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddHours(1));
        fingerprint.IsCurrent().Should().BeTrue();
    }

    [Test]
    public void IsCurrent_should_be_false_if_content_changed_with_same_size_and_time()
    {
        FileFingerprint fingerprint = Read();
        DateTime time = File.GetLastWriteTimeUtc(_file);
        File.WriteAllText(_file, "CONTENT");
        File.SetLastWriteTimeUtc(_file, time);

        fingerprint.IsCurrent().Should().BeFalse();
    }

    [Test]
    public void IsCurrent_should_be_false_if_file_is_deleted_or_created()
    {
        FileFingerprint fingerprint = Read();
        File.Delete(_file);
        fingerprint.IsCurrent().Should().BeFalse();
        File.WriteAllText(_file, "content");
        fingerprint.IsCurrent().Should().BeTrue();

        Directory.CreateDirectory(Path.GetDirectoryName(_missingFile)!);
        fingerprint.IsCurrent().Should().BeTrue();
        File.WriteAllText(_missingFile, "");
        fingerprint.IsCurrent().Should().BeFalse();
    }

    [Test]
    public void IsExisting_should_tell_missing_files()
    {
        FileFingerprint fingerprint = Read();
        fingerprint.IsExisting(_file).Should().BeTrue();
        fingerprint.IsExisting(_missingFile).Should().BeFalse();
        fingerprint.Contains(_missingFile).Should().BeTrue();
        fingerprint.Contains("other").Should().BeFalse();
    }

    [Test]
    public void State_should_be_unknown_if_file_cannot_be_read()
    {
        FileFingerprint fingerprint = Read();
        using (new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            FileFingerprint.TryRead([_file]).Should().BeNull();
            fingerprint.IsCurrent().Should().BeFalse();
        }

        FileFingerprint.TryRead([_directory]).Should().BeNull();

        using (FileStream stream = new(_file, FileMode.Open))
        {
            stream.SetLength(FileFingerprint.MaxFileSize + 1);
        }

        FileFingerprint.TryRead([_file]).Should().BeNull();
    }

    [Test]
    public void Comparison_should_consider_all_or_the_common_files()
    {
        FileFingerprint both = Read();
        FileFingerprint one = FileFingerprint.TryRead([_file])!;
        both.IsSame(Read()).Should().BeTrue();
        both.IsSame(one).Should().BeFalse();
        one.IsSame(both).Should().BeFalse();
        both.IsSameForCommonFiles(one).Should().BeTrue();

        File.WriteAllText(_file, "changed");
        both.IsSame(Read()).Should().BeFalse();
        Read().IsSameForCommonFiles(one).Should().BeFalse();
    }

    private FileFingerprint Read() => FileFingerprint.TryRead([_file, _missingFile, _file])!;
}
