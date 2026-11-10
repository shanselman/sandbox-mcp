using System;
using System.IO;

internal static class EvidencePublication
{
    public static void PublishNew(string destination, Action<Stream> write)
    {
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            // Never replace a previously published frame or receipt.
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
