package forge.api;

import java.nio.ByteBuffer;
import java.util.Locale;
import java.util.zip.CRC32;

/** Self-contained IPv4 endpoint with a typo checksum; no directory service or secret. */
final class NetworkInvite {
    private static final String ALPHABET = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    static String encode(String host, int port) {
        if (host == null || port < 1 || port > 65535) return null;
        String[] octets = host.trim().split("\\.", -1);
        if (octets.length != 4) return null;
        byte[] bytes = new byte[10];
        for (int i = 0; i < 4; i++) {
            if (!octets[i].matches("[0-9]{1,3}")) return null;
            int value = Integer.parseInt(octets[i]);
            if (value > 255) return null;
            bytes[i] = (byte) value;
        }
        ByteBuffer.wrap(bytes).putShort(4, (short) port);
        CRC32 crc = new CRC32();
        crc.update(bytes, 0, 6);
        ByteBuffer.wrap(bytes).putInt(6, (int) crc.getValue());
        StringBuilder result = new StringBuilder("MT1");
        for (int group = 0; group < 16; group++) {
            if (group % 4 == 0) result.append('-');
            int value = 0;
            for (int bit = 0; bit < 5; bit++) {
                int position = group * 5 + bit;
                value = (value << 1) | ((bytes[position / 8] >> (7 - position % 8)) & 1);
            }
            result.append(ALPHABET.charAt(value));
        }
        return result.toString();
    }

    static String address(String input) {
        String value = input.trim();
        if (!value.toUpperCase(Locale.ROOT).startsWith("MT1")) return value;
        String code = value.replaceAll("[\\s-]", "").toUpperCase(Locale.ROOT).substring(3);
        if (code.length() != 16) throw invalid();
        byte[] bytes = new byte[10];
        for (int group = 0; group < 16; group++) {
            int digit = ALPHABET.indexOf(code.charAt(group));
            if (digit < 0) throw invalid();
            for (int bit = 0; bit < 5; bit++) {
                int position = group * 5 + bit;
                bytes[position / 8] |= (byte) (((digit >> (4 - bit)) & 1) << (7 - position % 8));
            }
        }
        CRC32 crc = new CRC32();
        crc.update(bytes, 0, 6);
        if ((int) crc.getValue() != ByteBuffer.wrap(bytes).getInt(6)) throw invalid();
        int port = Short.toUnsignedInt(ByteBuffer.wrap(bytes).getShort(4));
        if (port == 0) throw invalid();
        return String.format(Locale.ROOT, "%d.%d.%d.%d:%d", bytes[0] & 255, bytes[1] & 255, bytes[2] & 255, bytes[3] & 255, port);
    }

    private static IllegalArgumentException invalid() {
        return new IllegalArgumentException("This invite looks incomplete or mistyped. Copy the full MT1 invite from the host.");
    }
}
