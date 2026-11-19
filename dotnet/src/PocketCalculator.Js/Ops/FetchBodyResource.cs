namespace PocketCalculator.Js.Ops;

/// <summary>
/// Rust <c>FetchBodyResource</c> (upstream 3aa2f59): the body of a <c>fetch()</c> response
/// that resolved with its headers, read in the background and handed to the shim by
/// <c>op_fetch_body</c>. <c>op_try_close</c> closes it, which stops the read.
/// </summary>
internal sealed class FetchBodyResource(CancellationTokenSource cancel, bool opaque)
{
    private int taken;
    private int closed;

    /// <summary>Cancels the read: the exchange deadline, the page's signal, or a close.</summary>
    /// <remarks>Never disposed: a close can race the read's end.</remarks>
    public CancellationTokenSource Cancel { get; } = cancel;

    /// <summary>An opaque response's body reaches script empty.</summary>
    public bool Opaque { get; } = opaque;

    /// <summary>The read; set once, before the id is returned to script.</summary>
    public Task<byte[]>? Body { get; set; }

    /// <summary>Whether script closed the body before it was read in full.</summary>
    public bool Closed => Volatile.Read(ref closed) != 0;

    /// <summary>Claims the body for <c>op_fetch_body</c>; false when it was claimed already.</summary>
    public bool Take() => Interlocked.Exchange(ref taken, 1) == 0;

    /// <summary>Stops the read, as Rust's <c>Resource::close</c> cancels its handle.</summary>
    public void Close()
    {
        Volatile.Write(ref closed, 1);
        Cancel.Cancel();
    }
}
