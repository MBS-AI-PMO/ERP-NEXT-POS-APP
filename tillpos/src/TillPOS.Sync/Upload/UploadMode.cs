using TillPOS.Erp;

namespace TillPOS.Sync.Upload;

/// <summary>Off: nothing is built or sent. DryRun: payloads are built and references checked read-only, and the JSON is
/// written for inspection; nothing is written to ERPNext. Live: documents are inserted and submitted.</summary>
public enum UploadMode { Off, DryRun, Live }

/// <summary>The write guard: a real <see cref="IErpWriter"/> exists only in Live mode. Everything that could write takes
/// its writer from here, so Off and DryRun cannot reach ERPNext even by mistake.</summary>
public static class UploadPipeline
{
    /// <summary>The writer the app may hand out: <paramref name="client"/> in Live mode, otherwise null.</summary>
    public static IErpWriter? LiveWriter(UploadMode mode, IErpWriter client) => mode == UploadMode.Live ? client : null;

    /// <summary>The writer an upload component uses. Off and DryRun must get no writer (they get <see cref="NoWriteErpWriter"/>,
    /// which throws); Live must get one.</summary>
    public static IErpWriter WriterFor(UploadMode mode, IErpWriter? writer)
    {
        if (mode == UploadMode.Live)
            return writer ?? throw new ArgumentNullException(nameof(writer), "Live upload needs an ERPNext writer.");
        if (writer is not null && writer is not NoWriteErpWriter)
            throw new ArgumentException($"Upload mode {mode} must not be given an ERPNext writer.", nameof(writer));
        return NoWriteErpWriter.Instance;
    }
}
