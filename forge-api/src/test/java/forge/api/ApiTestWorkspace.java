package forge.api;

import forge.StaticData;
import forge.localinstance.properties.ForgeConstants;

import java.io.File;
import java.io.IOException;
import java.io.UncheckedIOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Set;
import java.util.UUID;

/** Disposable process-owned test files. This is not a product profile override. */
final class ApiTestWorkspace {
    private static ApiTestWorkspace current;
    final Path root;
    final String trialId;
    final String mode;
    final String forkId;

    private ApiTestWorkspace(Path root, String trialId, String mode, String forkId) {
        this.root = root;
        this.trialId = trialId;
        this.mode = mode;
        this.forkId = forkId;
    }

    static synchronized ApiTestWorkspace current() {
        if (current == null) {
            String trial = System.getProperty("mana.tests.trialId", "local-" + UUID.randomUUID());
            Path runRoot = Path.of(System.getProperty("mana.tests.runRoot",
                    Path.of("target/rules-regressions/api-jvms", trial).toString()));
            current = create(runRoot, trial, System.getProperty("mana.tests.mode", "default"),
                    parseForkLabel(System.getProperty("mana.tests.forkLabel", "fork-1")));
        }
        return current;
    }

    static String parseForkLabel(String label) {
        if (label == null || !label.matches("fork-[1-9][0-9]{0,8}"))
            throw new IllegalArgumentException("Invalid API test fork label: [" + label + "]");
        return label.substring("fork-".length());
    }

    static ApiTestWorkspace create(Path runRoot, String trialId, String mode, String forkId) {
        label(trialId);
        if (!Set.of("default", "serial", "parallel").contains(mode))
            throw new IllegalArgumentException("Unknown API test mode: " + mode);
        if (forkId == null || !forkId.matches("[1-9][0-9]{0,8}"))
            throw new IllegalArgumentException("Invalid API test fork ID: [" + forkId + "]");
        try {
            Path owner = runRoot.toAbsolutePath().normalize().resolve(mode).resolve("fork-" + forkId);
            Files.createDirectories(owner);
            Path root = Files.createTempDirectory(owner, "process-" + ProcessHandle.current().pid() + "-").toRealPath();
            Files.createDirectories(root.resolve("evidence"));
            Files.createDirectories(root.resolve("profiles"));
            Files.createDirectories(root.resolve("bootstrap/engine-profile"));
            Files.createDirectories(root.resolve("bootstrap/engine-cache"));
            return new ApiTestWorkspace(root, trialId, mode, forkId);
        } catch (IOException failure) {
            throw new UncheckedIOException("Cannot create the API test workspace", failure);
        }
    }

    void bootstrap() {
        Path resources = Path.of("../forge-gui/res");
        // Set these before any resource initialization can memoize ForgeConstants.
        System.setProperty("forge.resources", resources.toAbsolutePath().normalize() + File.separator);
        System.setProperty("forge.userDir", root.resolve("bootstrap/engine-profile").toString());
        System.setProperty("forge.cacheDir", root.resolve("bootstrap/engine-cache").toString());
        if (StaticData.instance() == null) EngineResources.load(resources);
        HeadlessPlatform.initialize(resources, root.resolve("bootstrap"));
        try {
            if (!Files.isSameFile(Path.of(System.getProperty("forge.userDir")), root.resolve("bootstrap/engine-profile"))
                    || !Files.isSameFile(Path.of(System.getProperty("forge.cacheDir")), root.resolve("bootstrap/engine-cache"))
                    || !Files.isSameFile(Path.of(ForgeConstants.USER_DIR), root.resolve("bootstrap/engine-profile"))
                    || !Files.isSameFile(Path.of(ForgeConstants.CACHE_DIR), root.resolve("bootstrap/engine-cache"))
                    || !Files.isSameFile(Path.of(ForgeConstants.RES_DIR), resources))
                throw new IllegalStateException("The API test process was bootstrapped outside its workspace");
        } catch (IOException failure) { throw new UncheckedIOException(failure); }
    }

    static Path evidence(String name) { return current().directory("evidence", name); }
    static Path profile(String name) { return current().directory("profiles", name); }
    static Path evidenceFile(String name) { return evidence(name).resolve(label(name)); }

    Path directory(String category, String name) {
        if (!Set.of("evidence", "profiles").contains(category)) throw new IllegalArgumentException("Unknown workspace category");
        label(name);
        try {
            return Files.createTempDirectory(root.resolve(category), name + "-");
        } catch (IOException failure) {
            throw new UncheckedIOException(failure);
        }
    }

    private static String label(String value) {
        if (value == null || !value.matches("[A-Za-z0-9][A-Za-z0-9._-]{0,159}"))
            throw new IllegalArgumentException("Invalid API test workspace label");
        return value;
    }
}
