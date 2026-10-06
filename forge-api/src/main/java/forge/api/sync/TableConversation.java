package forge.api.sync;

import java.util.*;
import java.util.function.LongSupplier;

/** Table social state, independent of cards and rules. The connection owns sender identity. */
public final class TableConversation {
    public record Member(String id, int seat, String name) { }
    public record Message(long id, String senderId, int seat, String name, String kind, String text) { }
    public record Snapshot(String tableId, long revision, int localSeat, List<Member> members, List<Message> messages) { }
    public static final List<String> EMOTES = List.of("Hello!", "Good luck!", "Nice play!", "Thinking…", "Thanks!", "Good game!");
    private final String id = UUID.randomUUID().toString();
    private final Map<Integer, Member> members = new TreeMap<>();
    private final Deque<Message> messages = new ArrayDeque<>();
    private final Map<Integer, Long> lastSent = new HashMap<>();
    private final LongSupplier clock;
    private long revision;
    public TableConversation() { this(System::nanoTime); }
    TableConversation(LongSupplier clock) { this.clock = clock; }
    public String id() { return id; }
    public synchronized void join(int seat, String name) {
        if (members.containsKey(seat)) throw new IllegalStateException("This seat is already occupied");
        members.put(seat, new Member(UUID.randomUUID().toString(), seat, name));
        activity(seat, "joined the table.");
    }
    public synchronized String rename(int seat, String value) {
        String name = clean(value, 24, "Name"); var member = member(seat);
        if (members.values().stream().anyMatch(m -> m.seat() != seat && m.name().equalsIgnoreCase(name)))
            throw new IllegalArgumentException("That name is already at this table. Choose another name.");
        if (member.name().equals(name)) return name;
        members.put(seat, new Member(member.id(), seat, name));
        append(member, "system", "is now known as " + name + "."); return name;
    }
    public synchronized void leave(int seat) {
        activity(seat, "left the table."); members.remove(seat); lastSent.remove(seat);
    }
    public synchronized void activity(int seat, String text) { append(member(seat), "system", text); }
    public synchronized void send(int seat, String kind, String value) {
        var member = member(seat);
        if (!kind.equals("chat") && !kind.equals("emote")) throw new IllegalArgumentException("Unknown message type");
        String text = clean(value, 300, "Message");
        if (kind.equals("emote") && !EMOTES.contains(text)) throw new IllegalArgumentException("Unknown emote");
        long now = clock.getAsLong(); Long previous = lastSent.get(seat);
        if (previous != null && now - previous < 750_000_000L) throw new IllegalStateException("Give the table a moment before sending again.");
        lastSent.put(seat, now); append(member, kind, text);
    }
    public synchronized Snapshot snapshot(int viewer) {
        member(viewer); return new Snapshot(id, revision, viewer, List.copyOf(members.values()), List.copyOf(messages));
    }
    private Member member(int seat) {
        var member = members.get(seat); if (member == null) throw new IllegalStateException("This seat is no longer active"); return member;
    }
    private void append(Member member, String kind, String text) {
        messages.addLast(new Message(++revision, member.id(), member.seat(), member.name(), kind, text));
        while (messages.size() > 200) messages.removeFirst();
    }
    private static String clean(String value, int maximum, String label) {
        if (value == null || value.length() > maximum) throw new IllegalArgumentException(label + " must be 1–" + maximum + " characters.");
        String text = value.strip();
        if (text.isBlank() || text.codePoints().anyMatch(c -> Character.isISOControl(c) || Character.getType(c) == Character.FORMAT))
            throw new IllegalArgumentException(label + " must be a single line of visible text.");
        return text;
    }
}
