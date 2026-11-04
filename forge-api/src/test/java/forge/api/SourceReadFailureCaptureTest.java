package forge.api;

import org.testng.annotations.Test;

import java.io.ByteArrayOutputStream;
import java.io.OutputStream;
import java.io.PrintStream;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;

import static org.testng.Assert.*;

/** Private diagnostic resource/fault contracts, separate from actual source
 * input journeys. The same narrow primitive has independent seat/process owners. */
public class SourceReadFailureCaptureTest {
    @Test public void unicodeAndMalformedMessagesHaveAHardUtf8ByteLimitAndOnlyTheFirstFailureIsCaptured() throws Exception {
        var receipt = new Receipt();
        receipt.capture(new IllegalStateException("FIRST-" + "\uD83D\uDE00\u4F60\u597D\uD800".repeat(12000)));
        for (int index = 0; index < 100; index++) receipt.capture(new IllegalStateException("LATER-" + index));
        var output = new ByteArrayOutputStream(); var sink = new PrintStream(output, true, StandardCharsets.UTF_8);
        receipt.emit(sink);
        byte[] bytes = output.toByteArray();
        assertTrue(bytes.length <= 8192); assertTrue(bytes.length > 7000);
        String text = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
        assertTrue(text.contains("FIRST-")); assertTrue(text.contains("[truncated]")); assertFalse(text.contains("LATER-"));
        receipt.capture(new IllegalStateException("LATER-again")); receipt.emit(sink);
        assertEquals(output.toByteArray(), bytes, "The emitted first receipt is never replaced or repeated by a poll/failure flood");
    }

    @Test public void causeCyclesAndLargeStacksRespectTheirSeparateBounds() throws Exception {
        var first = new MutableCause("CAUSE-0"); var cursor = first;
        for (int index = 1; index < 7; index++) { cursor.next = new MutableCause("CAUSE-" + index); cursor = cursor.next; }
        cursor.next = first;
        var receipt = new Receipt(); receipt.capture(first); String text = receipt.text();
        for (int index = 0; index < 4; index++) assertTrue(text.contains("CAUSE-" + index));
        assertFalse(text.contains("CAUSE-4")); assertFalse(text.contains("frame16"));
        assertEquals(text.lines().filter(line -> line.startsWith("\tat ")).count(), 64L);
        var cycle = new MutableCause("SMALL-CYCLE"); cycle.next = cycle;
        var cyclic = new Receipt(); cyclic.capture(cycle);
        assertEquals(cyclic.text().lines().filter(line -> line.contains("SMALL-CYCLE")).count(), 1L);
    }

    @Test public void brokenFormattingAndSinkCannotThrowOrDisableAnotherSessionsCapture() throws Exception {
        var receipt = new Receipt();
        var original = new IllegalStateException("ORIGINAL") {
            @Override public String getMessage() { throw new IllegalStateException("Broken diagnostic message getter"); }
        };
        receipt.capture(original);
        assertTrue(receipt.text().contains("diagnostic unavailable"));
        var broken = new Receipt(); broken.capture(new IllegalStateException("FIRST"));
        broken.emit(new PrintStream(OutputStream.nullOutputStream()) {
            @Override public void write(byte[] buffer, int offset, int count) { throw new IllegalStateException("Broken private sink"); }
        });
        assertEquals(broken.text(), "", "A broken sink is not retried on every query");
        var independent = new Receipt(); independent.capture(new IllegalArgumentException("OTHER-SESSION"));
        assertTrue(independent.text().contains("OTHER-SESSION"));
    }

    private static final class MutableCause extends RuntimeException {
        MutableCause next;
        MutableCause(String message) {
            super(message); var frames = new StackTraceElement[32];
            for (int index = 0; index < frames.length; index++) frames[index] = new StackTraceElement("ReadScope", "frame" + index, "ReadScope.java", index);
            setStackTrace(frames);
        }
        @Override public synchronized Throwable getCause() { return next; }
    }
    private static final class Receipt {
        private final SourceReadFailureCapture owner = new SourceReadFailureCapture();
        void capture(Throwable failure) { owner.capture(failure); }
        void emit(PrintStream sink) { owner.emit(sink); }
        String text() throws Exception {
            var bytes = new ByteArrayOutputStream(); emit(new PrintStream(bytes, true, StandardCharsets.UTF_8)); return bytes.toString(StandardCharsets.UTF_8);
        }
    }
}
