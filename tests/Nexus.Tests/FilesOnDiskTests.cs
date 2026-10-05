using System.Text;
using Nexus.Agent;
using Nexus.Contracts;
using Nexus.Hub.Core;
using Nexus.Hub.Core.Files;
using OpenMcdf;

namespace Nexus.Tests;

[Collection("NexusHome")]
public sealed class FilesOnDiskTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-disk-" + Guid.NewGuid().ToString("N"));

    public FilesOnDiskTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable("NEXUS_HOME", Path.Combine(_dir, "home"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* ignored */ }
    }

    /// <summary>A stand-in .rvt: a compound file with a BasicFileInfo stream like Revit writes.</summary>
    private string FakeRevitFile(string name, string info)
    {
        string path = Path.Combine(_dir, name);
        using (var root = RootStorage.Create(path))
        using (var stream = root.CreateStream("BasicFileInfo"))
        {
            var bytes = new byte[] { 4, 0, 0, 0, 1, 0, 0, 0 }.Concat(Encoding.Unicode.GetBytes(info)).ToArray();
            stream.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    [Fact]
    public void Revit_version_and_worksharing_are_read_from_the_file()
    {
        string path = FakeRevitFile("Tower.rvt", "Worksharing: Central\r\nUsername: \r\nCentral Model Path: \\\\srv\\Tower.rvt\r\nFormat: 2025\r\nBuild: 20240307\r\n");
        var file = DiskFile.Create(path);
        file.Inspect();
        Assert.True(file.IsRevit);
        Assert.Equal(2025, file.RevitYear);
        Assert.True(file.IsWorkshared);
        Assert.Equal("Revit 2025 model · workshared", file.Format);

        var older = RevitFileVersion.Parse(Encoding.Unicode.GetBytes("Worksharing: Not enabled\r\nRevit Build: Autodesk Revit 2019 (Build: 20180806_1515(x64))"));
        Assert.Equal((2019, false), older);
    }

    [Fact]
    public void Drawing_format_is_read_from_the_header()
    {
        string path = Path.Combine(_dir, "C-101.dwg");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("AC1032").Concat(new byte[100]).ToArray());
        var file = DiskFile.Create(path);
        file.Inspect();
        Assert.False(file.IsRevit);
        Assert.Equal(HostKinds.AutoCAD, file.HostKind);
        Assert.Equal("AutoCAD 2018 drawing format", file.Format);
    }

    [Fact]
    public void Only_models_and_drawings_are_picked_up()
    {
        Assert.True(DiskFile.IsSupported(@"C:\DC\ACCDocs\Proj\Tower.rvt"));
        Assert.True(DiskFile.IsSupported(@"C:\x\C-101.DWG"));
        Assert.False(DiskFile.IsSupported(@"C:\x\Tower.0003.rvt"));
        Assert.False(DiskFile.IsSupported(@"C:\x\C-101.bak"));
        Assert.False(DiskFile.IsSupported(@"C:\x\Family.rfa"));

        Directory.CreateDirectory(Path.Combine(_dir, "sub", "Tower_backup"));
        File.WriteAllText(Path.Combine(_dir, "sub", "A.dwg"), "");
        File.WriteAllText(Path.Combine(_dir, "sub", "Tower.0001.rvt"), "");
        File.WriteAllText(Path.Combine(_dir, "sub", "Tower_backup", "Tower.rvt"), "");
        File.WriteAllText(Path.Combine(_dir, "B.rvt"), "");
        var found = DiskFile.FindIn(_dir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "A.dwg", "B.rvt" }, found);
    }

    [Fact]
    public void Pdf_names_are_safe_and_unique()
    {
        Assert.Equal("A101 - Plan_ Level 1", PdfNames.For(PdfNames.DefaultPattern, "A101", "Plan: Level 1", "Tower"));
        Assert.Equal("Layout1", PdfNames.For(PdfNames.DefaultPattern, "", "Layout1", "Site"));
        Assert.Equal("Site - C-1 - Grading", PdfNames.For("{File} - {Number} - {Name}", "C-1", "Grading", "Site"));
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Assert.EndsWith("A101.pdf", PdfNames.Unique(_dir, "A101", taken));
        Assert.EndsWith("A101 (2).pdf", PdfNames.Unique(_dir, "A101", taken));
    }

    [Fact]
    public void Results_are_reused_until_the_file_changes()
    {
        string path = Path.Combine(_dir, "C-101.dwg");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("AC1032").Concat(new byte[100]).ToArray());
        var file = DiskFile.Create(path);
        file.Inspect();
        var request = new ReadRequest { ReaderId = "acad.sheets", Options = { ["drawingProperties"] = "true" } };
        Assert.Null(ReadCache.Get(file, request));

        ReadCache.Put(file, request, new ReadResult { ReaderId = "acad.sheets", Items = { new DataItem { Id = "1F", Name = "Layout1" } } });
        Assert.Equal("Layout1", Assert.Single(ReadCache.Get(file, request)!.Items).Name);
        // Other options: not the same read.
        Assert.Null(ReadCache.Get(file, new ReadRequest { ReaderId = "acad.sheets" }));

        File.AppendAllText(path, "changed");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.True(file.Changed());
        file.Inspect();
        Assert.Null(ReadCache.Get(file, request));
    }

    [Fact]
    public async Task Work_copies_leave_the_original_alone_and_are_removed()
    {
        string path = Path.Combine(_dir, "Model.dwg");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        string copyPath;
        using (var copy = await WorkCopy.CreateAsync(path, CancellationToken.None))
        {
            copyPath = copy.Path;
            Assert.StartsWith(NexusPaths.OfflineWorkDir, copy.Path);
            File.WriteAllText(copy.Path, "changed by a program"); // the copy is writable
        }
        Assert.False(File.Exists(copyPath));
        Assert.Equal("original", File.ReadAllText(path));
        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public void Rows_from_files_on_disk_are_read_only()
    {
        var host = new HostInfo { HostKind = HostKinds.Revit, Product = "Revit", Features = { AgentFeatures.FileOnDisk } };
        var table = ResultTable.Build(new[]
        {
            new ResultSource
            {
                Host = host, DocumentTitle = "Tower.rvt",
                Result = new ReadResult
                {
                    ReaderId = "revit.sheets", DocumentId = "disk-1",
                    Items = { new DataItem { Id = "u1", ItemType = "Sheet", Name = "A101", Groups = { new PropertyGroup("Identity Data") { Properties = { new PropertyValue { Name = "Sheet Name", Id = "SHEET_NAME", Value = "Plan", Source = PropertySource.BuiltIn } } } } } },
                },
            },
        });
        var row = Assert.Single(table.Rows);
        string column = row.Values.Keys.Single();
        Assert.Contains("read-only", Editing.Blocker(row, column));
    }

    [Fact]
    public void Sheet_pdfs_combine_into_one()
    {
        string MakePdf(string name, int pages)
        {
            using var doc = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < pages; i++) doc.AddPage();
            string p = Path.Combine(_dir, name);
            doc.Save(p);
            return p;
        }
        string target = Path.Combine(_dir, "Set.pdf");
        PdfTools.Combine(new[] { MakePdf("A101.pdf", 1), MakePdf("A102.pdf", 2) }, target);
        using var combined = PdfSharp.Pdf.IO.PdfReader.Open(target, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        Assert.Equal(3, combined.PageCount);
    }

    [Fact]
    public async Task Agent_reads_files_that_are_not_open_and_closes_them()
    {
        var log = new AgentLog("test");
        using var host = new FakeHost(log);
        var readers = new ReaderRegistry<FakeDoc>();
        readers.Register(new FakeSheetsReader());
        var opener = new FakeOpener();
        var info = HostInfoFactory.Create("Fake", "FakeCAD", "2026", "test", log);
        info.HostKind = "Fake" + Guid.NewGuid().ToString("N")[..8];
        using var server = new AgentServer<FakeDoc>(info, host.Queue, new FakeDocs(), readers, log,
            new AgentServerOptions { WriteRegistration = false }, files: opener, pdf: opener);
        server.Start();
        await using var client = await AgentClient.ConnectAsync(server.PipeName, TimeSpan.FromSeconds(5));

        var hello = await client.HelloAsync();
        Assert.Contains(AgentFeatures.ReadFile, hello.Host.Features);
        Assert.Contains(AgentFeatures.ExportPdf, hello.Host.Features);

        string path = Path.Combine(_dir, "Closed.rvt");
        File.WriteAllText(path, "x");
        var result = await client.ReadFileAsync(new ReadFileRequest { Path = path, ReaderId = "fake.sheets", Options = { ["count"] = "2" } });
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("Closed.rvt", result.DocumentTitle);
        Assert.Equal(1, opener.Opened);
        Assert.Equal(1, opener.Closed);

        var pdf = await client.ExportPdfAsync(new ExportPdfRequest { Path = path, OutputFolder = Path.Combine(_dir, "pdf"), ItemIds = { "s1" } });
        Assert.Equal("s1", Assert.Single(pdf.Sheets).ItemId);
        Assert.Equal(2, opener.Closed);

        var missing = await Assert.ThrowsAsync<AgentRequestException>(() =>
            client.ReadFileAsync(new ReadFileRequest { Path = Path.Combine(_dir, "nope.rvt"), ReaderId = "fake.sheets" }));
        Assert.Equal(ErrorCodes.DocumentNotFound, missing.Code);
    }

    private sealed class FakeOpener : IFileOpener<FakeDoc>, IPdfExporter<FakeDoc>
    {
        public int Opened, Closed;

        public FakeDoc Open(string path, AgentLog log, out bool openedHere)
        {
            Opened++;
            openedHere = true;
            return new FakeDoc { Id = "bg", Title = Path.GetFileName(path) };
        }

        public void Close(FakeDoc document, AgentLog log) => Closed++;

        public ExportPdfResult Export(FakeDoc document, ExportPdfRequest request, AgentLog log, CancellationToken ct) => new()
        {
            Sheets = request.ItemIds.Select(id => new ExportedSheet { ItemId = id, PdfPath = Path.Combine(request.OutputFolder, id + ".pdf") }).ToList(),
        };
    }
}
