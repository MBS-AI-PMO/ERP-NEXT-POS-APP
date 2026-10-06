using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TillPOS.App;

/// <summary>Sends raw ESC/POS bytes to a Windows printer queue (no print dialog, no GDI rendering).</summary>
public static class RawPrinter
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocName = "TillPOS receipt";
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType = "RAW";
    }

    /// <summary>PRINTER_INFO_2W, complete so that Attributes and Status sit at the right offsets (pointer fields as IntPtr).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    private const uint PrinterStatusPaused = 0x1;
    private const uint PrinterStatusError = 0x2;
    private const uint PrinterStatusPaperJam = 0x8;
    private const uint PrinterStatusPaperOut = 0x10;
    private const uint PrinterStatusOffline = 0x80;
    private const uint PrinterStatusNotAvailable = 0x1000;
    private const uint PrinterStatusUserIntervention = 0x100000;
    private const uint PrinterStatusDoorOpen = 0x400000;
    private const uint PrinterAttributeWorkOffline = 0x400;

    private const uint NeedsAttention = PrinterStatusOffline | PrinterStatusError | PrinterStatusPaperOut | PrinterStatusPaperJam
        | PrinterStatusDoorOpen | PrinterStatusNotAvailable | PrinterStatusUserIntervention | PrinterStatusPaused;

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr handle);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    private static extern bool GetPrinter(IntPtr handle, int level, IntPtr buffer, int size, out int needed);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr handle, int level, [In] DocInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr handle);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr handle, byte[] bytes, int count, out int written);

    public static void Send(string printerName, byte[] data)
    {
        if (!OpenPrinter(printerName, out var handle, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Printer '{printerName}' not found");
        try
        {
            EnsureReady(handle);
            if (StartDocPrinter(handle, 1, new DocInfo()) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!StartPagePrinter(handle)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!WritePrinter(handle, data, data.Length, out var written) || written != data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Printer did not accept the receipt");
                EndPagePrinter(handle);
            }
            finally
            {
                EndDocPrinter(handle);
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>The spooler happily queues a job for an offline printer, so ask for its state first and report a problem
    /// instead of "printing" into the queue. USB thermal printers' status reporting varies by driver (some always report
    /// ready); verify on the real printer.</summary>
    private static void EnsureReady(IntPtr handle)
    {
        GetPrinter(handle, 2, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Printer status could not be read");
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetPrinter(handle, 2, buffer, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Printer status could not be read");
            var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
            if ((info.Status & NeedsAttention) != 0 || (info.Attributes & PrinterAttributeWorkOffline) != 0)
                throw new InvalidOperationException("Printer is offline or needs attention");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
