package forge.api;

import java.util.Collections;
import java.util.IdentityHashMap;

/** One bounded private failure receipt per source-read owner, never a rules journal or
 * protocol field. Diagnostic work cannot replace the failed query's cause. */
final class SourceReadFailureCapture {
    private static final int MAX_BYTES = 8192, MAX_CAUSES = 4, MAX_FRAMES = 16;
    private static final byte[] CAPTURING = new byte[0], EMITTED = new byte[0];
    private static final byte[] UNAVAILABLE = "First unexpected source-read failure: private diagnostic unavailable.\n".getBytes(java.nio.charset.StandardCharsets.UTF_8);
    private final java.util.concurrent.atomic.AtomicReference<byte[]> first = new java.util.concurrent.atomic.AtomicReference<>();
    void capture(Throwable error) {
        if (!first.compareAndSet(null, CAPTURING)) return;
        byte[] receipt;
        try {
            var output = new BoundedUtf8(); output.append("First unexpected source-read failure (private):\n");
            var visited = Collections.newSetFromMap(new IdentityHashMap<Throwable, Boolean>());
            for (int cause = 0; error != null && cause < MAX_CAUSES && visited.add(error) && !output.full; cause++) {
                if (cause > 0) output.append("Caused by: ");
                output.append(error.getClass().getName()); output.append(": "); output.append(error.getMessage()); output.append("\n");
                var stack = error.getStackTrace();
                for (int frame = 0; stack != null && frame < stack.length && frame < MAX_FRAMES && !output.full; frame++) {
                    output.append("\tat "); output.append(String.valueOf(stack[frame])); output.append("\n");
                }
                error = error.getCause();
            }
            receipt = output.bytes();
        } catch (Throwable diagnosticFailure) {
            receipt = UNAVAILABLE;
        }
        first.set(receipt);
    }
    void emit(java.io.PrintStream sink) {
        byte[] receipt = first.get();
        if (receipt == null || receipt == CAPTURING || receipt == EMITTED || !first.compareAndSet(receipt, EMITTED)) return;
        try { sink.write(receipt); sink.flush(); }
        catch (Throwable diagnosticFailure) { /* Preserve the original query outcome, even with a broken sink. */ }
    }
    private static final class BoundedUtf8 {
        private static final byte[] TRUNCATED = "\n[truncated]\n".getBytes(java.nio.charset.StandardCharsets.UTF_8);
        private final java.io.ByteArrayOutputStream output = new java.io.ByteArrayOutputStream(MAX_BYTES);
        private boolean full;
        void append(String value) {
            if (full || value == null) return;
            int remaining = MAX_BYTES - TRUNCATED.length - output.size();
            var buffer = java.nio.ByteBuffer.allocate(remaining);
            int count = Math.min(value.length(), remaining);
            var text = java.nio.CharBuffer.wrap(value, 0, count);
            var encoder = java.nio.charset.StandardCharsets.UTF_8.newEncoder()
                    .onMalformedInput(java.nio.charset.CodingErrorAction.REPLACE)
                    .onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPLACE);
            var result = encoder.encode(text, buffer, true);
            output.write(buffer.array(), 0, buffer.position());
            full = result.isOverflow() || count < value.length();
        }
        byte[] bytes() {
            if (full) output.write(TRUNCATED, 0, TRUNCATED.length);
            return output.toByteArray();
        }
    }
}
