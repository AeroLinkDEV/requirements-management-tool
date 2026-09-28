package aerolink.integrity;

import com.google.gson.*;
import java.nio.file.*;
import java.util.*;

/** File-backed capture evidence, available only through the explicit fixture command. */
final class FixtureSource implements Source {
    private final JsonObject config;
    private final Path root;
    FixtureSource(JsonObject config, Path configDirectory) {
        this.config = config; root = configDirectory.resolve(Extractor.text(config, "fixtureDirectory")).normalize();
    }
    public List<String> enumerate() throws Exception {
        return Extractor.strings(JsonParser.parseString(Files.readString(root.resolve("inventory.json"))).getAsJsonArray());
    }
    public JsonObject schema() throws Exception { return JsonParser.parseString(Files.readString(root.resolve("schema.json"))).getAsJsonObject(); }
    public JsonArray users() throws Exception { return JsonParser.parseString(Files.readString(root.resolve("users.json"))).getAsJsonArray(); }
    public JsonObject item(String id, Path capture) throws Exception {
        JsonObject item = JsonParser.parseString(Files.readString(root.resolve(id + ".json"))).getAsJsonObject();
        int ordinal = 0;
        for (JsonElement entry : item.getAsJsonArray("attachments")) {
            JsonObject attachment = entry.getAsJsonObject();
            Path source = root.resolve(Extractor.text(attachment, "fixtureFile")).normalize();
            if (!source.startsWith(root) || !Files.isRegularFile(source, LinkOption.NOFOLLOW_LINKS)) throw new IllegalArgumentException("Invalid fixture attachment path.");
            Path target = capture.resolve("attachment-" + ordinal++);
            Files.copy(source, target, StandardCopyOption.REPLACE_EXISTING);
            attachment.remove("fixtureFile"); attachment.addProperty("localFile", target.toString());
        }
        return item;
    }
    public void close() { }
}
