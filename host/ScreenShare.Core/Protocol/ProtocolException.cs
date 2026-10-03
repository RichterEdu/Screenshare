namespace ScreenShare.Core.Protocol;

/// <summary>Bytes recebidos (ou uma mensagem a enviar) violam o protocolo — ver docs/protocol.md.</summary>
public sealed class ProtocolException(string message) : IOException(message);
