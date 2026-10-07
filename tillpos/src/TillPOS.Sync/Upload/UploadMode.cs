using TillPOS.Erp;

namespace TillPOS.Sync.Upload;

/// <summary>Off: nothing is built or sent. DryRun: payloads are built and references checked read-only, and the JSON is
/// written for inspection; nothing is written to ERPNext. Live: documents are inserted and submitted.</summary>
public enum UploadMode { Off, DryRun, Live }

/// <summary>The write guard: a real <see cref="IErpWriter"/> exists only in Live mode, and never in a test build (local test
/// cashiers or the sample QR). Everything that could write takes its writer from here, so Off, DryRun and test builds cannot
/// reach ERPNext even by mistake.</summary>
public static class UploadPipeline
{
    /// <summary>The writer the app may hand out: <paramref name="create"/>() in Live mode of a production build, otherwise null
    /// (the writer is not even built).</summary>
    public static IErpWriter? LiveWriter(UploadMode mode, Func<IErpWriter> create, bool testBuild = false) =>
        mode == UploadMode.Live && !testBuild ? create() : null;

    /// <summary>The writer an upload component uses. Off and DryRun must get no writer (they get <see cref="NoWriteErpWriter"/>,
    /// which throws); Live must get one, and is refused in a test build.</summary>
    public static IErpWriter WriterFor(UploadMode mode, IErpWriter? writer, bool testBuild = false)
    {
        if (mode == UploadMode.Live)
        {
            if (testBuild) throw new InvalidOperationException("Live upload is not available in test builds.");
            return writer ?? throw new ArgumentNullException(nameof(writer), "Live upload needs an ERPNext writer.");
        }
        if (writer is not null && writer is not NoWriteErpWriter)
            throw new ArgumentException($"Upload mode {mode} must not be given an ERPNext writer.", nameof(writer));
        return NoWriteErpWriter.Instance;
    }
}
