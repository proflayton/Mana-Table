package forge.api;

import org.testng.annotations.Test;
import static org.testng.Assert.*;

public class NetworkInviteTest {
    @Test public void roundTripsEndpointsAndToleratesPastedFormatting() {
        for (String host : new String[]{"127.0.0.1", "192.168.1.10", "203.0.113.42", "255.255.255.255"}) {
            for (int port : new int[]{1, 36743, 65535}) {
                String invite = NetworkInvite.encode(host, port);
                assertTrue(invite.matches("MT1(-[A-Z2-7]{4}){4}"));
                assertEquals(NetworkInvite.address(invite), host + ":" + port);
                assertEquals(NetworkInvite.address("  " + invite.toLowerCase().replace('-', ' ') + "  "), host + ":" + port);
            }
        }
        assertEquals(NetworkInvite.address("  localhost:36743 "), "localhost:36743");
    }

    @Test public void detectsTyposAndInvalidAddressesWithoutDnsLookups() {
        String invite = NetworkInvite.encode("203.0.113.42", 36743);
        for (int i = 4; i < invite.length(); i++) {
            if (invite.charAt(i) == '-') continue;
            String typo = invite.substring(0, i) + (invite.charAt(i) == 'A' ? 'B' : 'A') + invite.substring(i + 1);
            expectThrows(IllegalArgumentException.class, () -> NetworkInvite.address(typo));
        }
        expectThrows(IllegalArgumentException.class, () -> NetworkInvite.address("MT1-ABC"));
        assertNull(NetworkInvite.encode("256.1.1.1", 36743));
        assertNull(NetworkInvite.encode("example.com", 36743));
        assertNull(NetworkInvite.encode("1.1.1.1", 0));
    }
}
