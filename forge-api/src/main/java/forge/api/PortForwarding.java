package forge.api;

import forge.gamemodes.net.server.FServerManager;
import org.jupnp.UpnpServiceImpl;
import org.jupnp.registry.Registry;
import org.jupnp.model.meta.Device;
import org.jupnp.support.igd.PortMappingListener;
import org.jupnp.support.model.PortMapping;
import java.util.concurrent.*;

/** Optional reachability service; independent of lobby, rules and synchronization. */
final class PortForwarding implements AutoCloseable {
    private volatile String status;
    private volatile boolean closed;
    private UpnpServiceImpl service;
    PortForwarding(int port, boolean enabled) { status = enabled ? "pending" : "disabled"; if (enabled) CompletableFuture.runAsync(() -> start(port)); }
    private synchronized void start(int port) {
        if (closed) return;
        try {
            service = new UpnpServiceImpl(new org.jupnp.DefaultUpnpServiceConfiguration()); service.startup();
            service.getRegistry().addListener(new PortMappingListener(new PortMapping(port, FServerManager.getLocalAddress(), PortMapping.Protocol.TCP, "Mana Table")) {
                @Override public synchronized void deviceAdded(Registry registry, Device device) { super.deviceAdded(registry, device); if (!closed && !activePortMappings.isEmpty()) status = "mapped"; }
                @Override protected void handleFailureMessage(String message) { if (!closed) status = "failed"; }
            });
            service.getControlPoint().search();
            CompletableFuture.delayedExecutor(5, TimeUnit.SECONDS).execute(() -> { if (status.equals("pending")) status = "failed"; });
        } catch (Exception | LinkageError failure) { status = "failed"; }
    }
    String status() { return status; }
    @Override public synchronized void close() { closed = true; if (service != null) service.shutdown(); status = "disabled"; }
}
