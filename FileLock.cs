namespace DecompilerMCPServer;

/// <summary>
/// Cross-process mutual exclusion through an exclusively opened lock file. FileShare.None maps to an advisory lock
/// on Unix. Dispose the returned stream to release the lock.
/// </summary>
internal static class FileLock
{
    public static FileStream Acquire(string lockPath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            }
            catch (IOException ex)
            {
                // Only a lock held by another process is worth waiting for; if the file could not even be created
                // (path too long, folder gone, disk full) the error is real and must surface
                if (!File.Exists(lockPath))
                    throw;
                if (DateTime.UtcNow - started > timeout)
                    throw new IOException($"Timed out after {timeout.TotalMinutes:N0} minutes waiting for '{lockPath}'; " +
                                          $"another process seems stuck holding it. ({ex.Message})", ex);
                cancellationToken.WaitHandle.WaitOne(250);
            }
        }
    }
}
