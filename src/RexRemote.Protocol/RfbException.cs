namespace RexRemote.Protocol;

/// <summary>RFB プロトコル処理で発生した例外の基底クラス。</summary>
public class RfbException : Exception
{
    public RfbException(string message) : base(message) { }
    public RfbException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>サーバーから想定外のデータを受け取った。</summary>
public sealed class RfbProtocolException : RfbException
{
    public RfbProtocolException(string message) : base(message) { }
}

/// <summary>認証に失敗した、または使える認証方式が無い。</summary>
public sealed class RfbAuthenticationException : RfbException
{
    public RfbAuthenticationException(string message) : base(message) { }
}
