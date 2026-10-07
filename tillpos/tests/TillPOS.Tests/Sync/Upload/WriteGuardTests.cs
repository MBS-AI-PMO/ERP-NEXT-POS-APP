using TillPOS.Erp;
using TillPOS.Sync.Upload;
using TillPOS.Tests.Fakes;

namespace TillPOS.Tests.Sync.Upload;

public class WriteGuardTests
{
    [Fact]
    public async Task No_write_writer_throws_on_every_call()
    {
        var writer = NoWriteErpWriter.Instance;

        var insert = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.InsertAsync("POS Invoice", new { }));
        var submit = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.SubmitAsync("POS Invoice", "ACC-PSINV-1"));
        var call = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.CallAsync("frappe.client.insert", new { }));

        Assert.All(new[] { insert, submit, call }, ex => Assert.Equal("Upload is off", ex.Message));
    }

    [Fact]
    public void Read_interface_has_no_write_methods()
    {
        var names = typeof(IErpClient).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Insert") || n.Contains("Submit") || n.Contains("Delete") || n.Contains("Call"));
        Assert.False(typeof(IErpWriter).IsAssignableFrom(typeof(ReadOnlyErpClient)));
        Assert.False(typeof(IErpWriter).IsAssignableFrom(typeof(ErpClient)));
        Assert.DoesNotContain(typeof(ErpClient).GetMethods(), m => m.Name is "InsertAsync" or "SubmitAsync" or "CallAsync" or "DeleteAsync");
        Assert.DoesNotContain(typeof(ErpWriter).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance), _ => true);
    }

    [Fact]
    public void Read_only_client_cannot_be_cast_to_a_writer()
    {
        IErpClient reader = new ReadOnlyErpClient(new FakeErp());
        Assert.Null(reader as IErpWriter);
    }

    [Theory]
    [InlineData(UploadMode.Off)]
    [InlineData(UploadMode.DryRun)]
    public void Off_and_dry_run_never_get_a_real_writer(UploadMode mode)
    {
        var client = new FakeErp();

        Assert.Null(UploadPipeline.LiveWriter(mode, () => throw new InvalidOperationException("must not be built")));
        Assert.Same(NoWriteErpWriter.Instance, UploadPipeline.WriterFor(mode, null));
        Assert.Throws<ArgumentException>(() => UploadPipeline.WriterFor(mode, client));
    }

    [Fact]
    public void Live_gets_the_client_as_writer_and_needs_one()
    {
        var client = new FakeErp();

        Assert.Same(client, UploadPipeline.LiveWriter(UploadMode.Live, () => client));
        Assert.Same(client, UploadPipeline.WriterFor(UploadMode.Live, client));
        Assert.Throws<ArgumentNullException>(() => UploadPipeline.WriterFor(UploadMode.Live, null));
    }

    [Fact]
    public void A_test_build_never_gets_a_live_writer()
    {
        var client = new FakeErp();

        Assert.Null(UploadPipeline.LiveWriter(UploadMode.Live, () => client, testBuild: true));
        Assert.Throws<InvalidOperationException>(() => UploadPipeline.WriterFor(UploadMode.Live, client, testBuild: true));
        Assert.Same(NoWriteErpWriter.Instance, UploadPipeline.WriterFor(UploadMode.DryRun, null, testBuild: true));
    }
}
