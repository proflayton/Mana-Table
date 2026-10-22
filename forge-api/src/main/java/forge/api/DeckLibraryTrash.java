package forge.api;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Comparator;
import java.util.HexFormat;
import java.util.List;
import java.util.UUID;

/** Saved deck files only. Workshop, analysis and playtest documents retain their IDs. */
final class DeckLibraryTrash {
    record Entry(String deckId, String restoreToken, long deletedAt, Path path) { }
    private final Path decks, trash;

    DeckLibraryTrash(Path decks) {
        this.decks = decks.toAbsolutePath().normalize();
        trash = this.decks.resolve(".trash");
    }

    static String checkedId(String value) {
        if (value == null || !UUID.fromString(value).toString().equals(value))
            throw new IllegalArgumentException("Invalid deck or restore ID");
        return value;
    }

    static String token(byte[] bytes) {
        try { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes)); }
        catch (NoSuchAlgorithmException error) { throw new IllegalStateException(error); }
    }

    byte[] readSaved(String id) throws IOException {
        Path source = saved(id);
        requireRegular(source, "The saved deck is unavailable. Refresh Your decks.");
        return Files.readAllBytes(source);
    }

    Entry remove(String id, String expectedToken) throws IOException {
        Path source = saved(id);
        byte[] bytes = readSaved(id);
        if (expectedToken == null || !token(bytes).equals(expectedToken))
            throw new IllegalArgumentException("The saved deck changed. Refresh Your decks before deleting it.");
        var existing = entries();
        if (existing.stream().anyMatch(entry -> entry.deckId().equals(id)))
            throw new IllegalStateException("A removed copy of this deck already exists. Restore it before deleting again.");
        ensureTrash();
        long latest = existing.stream().mapToLong(Entry::deletedAt).max().orElse(0);
        long deletedAt = Math.max(System.currentTimeMillis(), Math.addExact(latest, 1));
        String restoreToken = UUID.randomUUID().toString();
        Path destination = trash.resolve(id + "." + deletedAt + "." + restoreToken + ".json");
        // Do not use REPLACE_EXISTING or ATOMIC_MOVE's provider-specific
        // replacement semantics: neither removal nor Undo may overwrite a file.
        Files.move(source, destination);
        return new Entry(id, restoreToken, deletedAt, destination);
    }

    void restore(String id, String restoreToken) throws IOException {
        Path destination = saved(id);
        checkedId(restoreToken);
        var matching = entries().stream().filter(entry -> entry.deckId().equals(id)
                && entry.restoreToken().equals(restoreToken)).toList();
        if (matching.size() != 1) throw new IllegalArgumentException("That removed deck is unavailable or ambiguous. Refresh Your decks.");
        if (Files.exists(destination, LinkOption.NOFOLLOW_LINKS))
            throw new IllegalStateException("A saved deck already uses this ID. Undo will not overwrite it.");
        requireRegular(matching.get(0).path(), "That removed deck is unavailable. Refresh Your decks.");
        Files.move(matching.get(0).path(), destination);
    }

    List<Entry> entries() throws IOException {
        if (!Files.exists(trash, LinkOption.NOFOLLOW_LINKS)) return List.of();
        requireTrash();
        try (var files = Files.list(trash)) {
            return files.filter(path -> Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS))
                    .map(this::entry).filter(java.util.Objects::nonNull)
                    .sorted(Comparator.comparingLong(Entry::deletedAt).reversed().thenComparing(Entry::restoreToken)).toList();
        }
    }

    static boolean contains(Path decks, String id) throws IOException {
        checkedId(id);
        return new DeckLibraryTrash(decks).entries().stream().anyMatch(entry -> entry.deckId().equals(id));
    }

    private Entry entry(Path path) {
        String[] parts = path.getFileName().toString().split("\\.");
        if (parts.length != 4 || !parts[3].equals("json")) return null;
        try {
            checkedId(parts[0]); checkedId(parts[2]);
            long deletedAt = Long.parseLong(parts[1]);
            if (deletedAt <= 0 || !Long.toString(deletedAt).equals(parts[1])) return null;
            return new Entry(parts[0], parts[2], deletedAt, path);
        } catch (IllegalArgumentException error) { return null; }
    }

    private Path saved(String id) { return decks.resolve(checkedId(id) + ".json"); }
    private void ensureTrash() throws IOException {
        if (!Files.exists(trash, LinkOption.NOFOLLOW_LINKS)) Files.createDirectory(trash);
        requireTrash();
    }
    private void requireTrash() throws IOException {
        if (Files.isSymbolicLink(trash) || !Files.isDirectory(trash, LinkOption.NOFOLLOW_LINKS))
            throw new IOException("The private removed-deck directory is not a regular directory.");
    }
    private static void requireRegular(Path path, String message) throws IOException {
        if (Files.isSymbolicLink(path) || !Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS))
            throw new IOException(message);
    }
}
