using System.Buffers.Binary;

namespace ScreenShare.Core.Protocol;

/// <summary>Lê mensagens completas de um stream (ex.: NetworkStream), remontando quadros que o TCP entregou em pedaços.</summary>
public sealed class MessageReader(Stream stream)
{
    private readonly byte[] _header = new byte[MessageCodec.HeaderSize];

    /// <summary>Próxima mensagem, ou null se o stream terminou exatamente entre mensagens.</summary>
    /// <exception cref="EndOfStreamException">O stream terminou no meio de uma mensagem.</exception>
    /// <exception cref="ProtocolException">Os bytes recebidos violam o protocolo.</exception>
    public async Task<Message?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var read = await stream.ReadAtLeastAsync(_header, _header.Length, throwOnEndOfStream: false, cancellationToken);
        if (read == 0)
            return null;
        if (read < _header.Length)
            throw new EndOfStreamException("Stream terminou dentro do cabeçalho de uma mensagem.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(1));
        if (length > MessageCodec.MaxPayloadLength)
            throw new ProtocolException($"Payload de {length} bytes excede o limite de {MessageCodec.MaxPayloadLength}.");

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return MessageCodec.Decode(_header[0], payload);
    }
}
