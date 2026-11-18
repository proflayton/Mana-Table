package forge.api;

import com.google.gson.GsonBuilder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;

/** Offline public definition export. Card names select input, never infer effects. */
public final class AttachmentCapabilityExport {
    public static void main(String[] arguments) throws Exception {
        if (arguments.length < 3 || arguments.length > 4)
            throw new IllegalArgumentException("Usage: AttachmentCapabilityExport <forge-gui/res> <card-name> <output.json> [face-index]");
        var data = EngineResources.load(Path.of(arguments[0]));
        var card = data.getCommonCards().getCard(arguments[1]);
        if (card == null) card = data.getVariantCards().getCard(arguments[1]);
        if (card == null) throw new IllegalArgumentException("The selected catalog definition does not exist");
        int face = arguments.length == 4 ? Integer.parseInt(arguments[3]) : 0;
        var faces = card.getRules().getAllFaces();
        if (face < 0 || face >= faces.size()) throw new IllegalArgumentException("The selected face does not exist");
        var output = Path.of(arguments[2]);
        var json = new GsonBuilder().serializeNulls().setPrettyPrinting().disableHtmlEscaping().create();
        Files.writeString(output, json.toJson(AttachmentCapabilities.export(faces.get(face))) + "\n", StandardCharsets.UTF_8);
    }
    private AttachmentCapabilityExport() { }
}
