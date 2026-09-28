package aerolink.integrity;

import com.google.gson.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;
import java.time.OffsetDateTime;
import java.util.*;
import java.util.zip.*;

/** #1186: capture under an operator-confirmed source freeze, then seal a complete scoped package. */
public final class Extractor {
    static final Gson JSON = new GsonBuilder().disableHtmlEscaping().serializeNulls().create();
    static final long MAX_BYTES = 100L * 1024 * 1024;
    private Extractor() { }

    public static void main(String[] args) throws Exception {
        if (args.length != 4 || !(args[0].equals("extract") || args[0].equals("fixture"))) {
            System.err.println("Usage: Extractor extract|fixture <config.json> <owned-checkpoint-directory> <new-output.zip>");
            System.exit(2); return;
        }
        Path configPath = Path.of(args[1]).toAbsolutePath();
        JsonObject config = JsonParser.parseString(Files.readString(configPath)).getAsJsonObject();
        boolean fixture = args[0].equals("fixture");
        Path work = Path.of(args[2]).toAbsolutePath().normalize();
        Path output = Path.of(args[3]).toAbsolutePath().normalize();
        validate(config, fixture);
        if (Files.exists(output) || Files.exists(Path.of(output + ".manifest.sha256"))) throw new IOException("Output already exists; never replace an acquired package.");
        Files.createDirectories(work);
        if (Files.isSymbolicLink(work)) throw new IOException("Checkpoint directory must not be a link.");
        String fingerprint = hash(JSON.toJson(config).getBytes(StandardCharsets.UTF_8));
        Path marker = work.resolve("configuration.sha256");
        if (Files.exists(marker)) {
            if (!Files.readString(marker).equals(fingerprint)) throw new IOException("Checkpoint configuration changed. Use a new directory.");
        } else {
            try (var entries = Files.list(work)) { if (entries.findAny().isPresent()) throw new IOException("An unowned checkpoint directory is not reusable."); }
            Files.writeString(marker, fingerprint, StandardOpenOption.CREATE_NEW);
        }
        Path members = work.resolve("members"); Files.createDirectories(members);
        try (Source source = fixture ? new FixtureSource(config, configPath.getParent()) : new MksSource(config)) {
            List<String> expected = strings(config.getAsJsonArray("itemIds"));
            checkInventory(expected, source.enumerate());
            JsonObject schema = source.schema(); JsonArray users = source.users();
            writeStable(members.resolve("schema.json"), JSON.toJson(schema));
            writeStable(members.resolve("users.json"), JSON.toJson(users));
            JsonObject capabilities = new JsonObject();
            capabilities.addProperty("mode", fixture ? "fixture" : qualified(config) ? "qualified" : "server-qualification-pending");
            capabilities.addProperty("adapterVersion", "1");
            capabilities.addProperty("configurationSha256", fingerprint);
            if (!fixture) {
                capabilities.add("responseProfile", config.get("profile"));
                capabilities.addProperty("profileSha256", profileHash(config));
                capabilities.addProperty("sdkSha256", sdkHash());
            }
            capabilities.add("operatorEvidence", config.get("qualification"));
            writeStable(members.resolve("capabilities.json"), JSON.toJson(capabilities));
            for (String id : expected) {
                Path checkpoint = work.resolve("item-" + id + ".sha256");
                Path itemPath = members.resolve("items/" + id + ".json");
                if (Files.exists(checkpoint) && Files.exists(itemPath)) {
                    if (!hash(Files.readAllBytes(itemPath)).equals(Files.readString(checkpoint))) throw new IOException("An item checkpoint was modified.");
                    JsonObject retained = JsonParser.parseString(Files.readString(itemPath)).getAsJsonObject();
                    verifyAttachments(retained, members);
                    continue;
                }
                Path capture = work.resolve("capture-" + id); Files.createDirectories(capture);
                JsonObject item = source.item(id, capture);
                if (!id.equals(text(item, "id"))) throw new IOException("Source returned a different item identity.");
                if (!strings(config.getAsJsonArray("projects")).contains(text(item, "project"))
                        || !strings(config.getAsJsonArray("types")).contains(text(item, "type"))) throw new IOException("Item outside accepted scope.");
                item.addProperty("snapshot", text(config, "capturedAt"));
                for (JsonElement entry : item.getAsJsonArray("attachments")) {
                    JsonObject attachment = entry.getAsJsonObject();
                    String local = text(attachment, "localFile");
                    Path captured = Path.of(local).toAbsolutePath().normalize();
                    if (!captured.startsWith(capture) || !Files.isRegularFile(captured, LinkOption.NOFOLLOW_LINKS)) throw new IOException("Attachment escaped its capture directory.");
                    if (Files.size(captured) == 0 || Files.size(captured) > MAX_BYTES) throw new IOException("Attachment size unsupported.");
                    byte[] bytes = Files.readAllBytes(captured); String digest = hash(bytes);
                    String path = "attachments/" + digest;
                    Path target = members.resolve(path); Files.createDirectories(target.getParent());
                    if (!Files.exists(target)) Files.write(target, bytes, StandardOpenOption.CREATE_NEW);
                    else if (!hash(Files.readAllBytes(target)).equals(digest)) throw new IOException("An attachment checkpoint was modified.");
                    attachment.remove("localFile"); attachment.addProperty("path", path);
                }
                writeStable(itemPath, JSON.toJson(item));
                Files.writeString(checkpoint, hash(Files.readAllBytes(itemPath)), StandardOpenOption.CREATE, StandardOpenOption.TRUNCATE_EXISTING);
                System.out.println("Captured item " + id + " (" + text(config, "sourceName") + ")");
            }
            // A freeze is an operator assertion; these checks detect changes but never manufacture that assertion.
            // Re-read every item AND attachment, including resumed items, before sealing the package.
            for (String id : expected) {
                Path verification = work.resolve("verify-" + id + "-" + UUID.randomUUID()); Files.createDirectory(verification);
                JsonObject observed = source.item(id, verification);
                observed.addProperty("snapshot", text(config, "capturedAt"));
                for (JsonElement entry : observed.getAsJsonArray("attachments")) {
                    JsonObject attachment = entry.getAsJsonObject();
                    Path captured = Path.of(text(attachment, "localFile")).toAbsolutePath().normalize();
                    if (!captured.startsWith(verification) || !Files.isRegularFile(captured, LinkOption.NOFOLLOW_LINKS)
                            || Files.size(captured) > MAX_BYTES) throw new IOException("Invalid verification attachment.");
                    attachment.remove("localFile"); attachment.addProperty("path", "attachments/" + hash(Files.readAllBytes(captured)));
                }
                JsonObject retained = JsonParser.parseString(Files.readString(members.resolve("items/" + id + ".json"))).getAsJsonObject();
                if (!retained.equals(observed)) throw new IOException("Source item or attachment changed: " + id + ". A new freeze and capture are required.");
            }
            checkInventory(expected, source.enumerate());
            if (!schema.equals(source.schema()) || !users.equals(source.users())) throw new IOException("Source configuration changed during extraction.");
            JsonObject manifest = new JsonObject(); manifest.addProperty("version", 1);
            for (String name : List.of("sourceInstanceId", "sourceName", "serverVersion", "query", "capturedAt")) manifest.add(name, config.get(name));
            // The adapter cannot certify itself. Real packages remain unproven until a target-server qualification is supplied.
            String mode = fixture ? "fixture" : qualified(config) ? "qualified" : "unproven";
            manifest.addProperty("evidenceMode", mode); manifest.addProperty("completeness", "complete-within-scope");
            JsonObject scope = new JsonObject();
            for (String name : List.of("projects", "types", "itemIds", "inventoryAcceptedBy", "freezeReference")) scope.add(name, config.get(name));
            scope.addProperty("historyDepth", "full-audit-history");
            scope.addProperty("relationshipPolicy", "preserve-references-without-following");
            scope.addProperty("attachmentDepth", "current-and-any-admin-inventoried-historical-bytes");
            manifest.add("scope", scope); manifest.add("findings", new JsonArray());
            JsonArray inventory = new JsonArray(); long expanded = 0;
            List<Path> paths;
            try (var stream = Files.walk(members)) { paths = stream.filter(Files::isRegularFile).sorted().toList(); }
            for (Path path : paths) {
                if (Files.isSymbolicLink(path)) throw new IOException("A package member is a link.");
                String name = members.relativize(path).toString().replace('\\', '/');
                if (!name.matches("[a-z0-9][a-z0-9._/-]*")) throw new IOException("Noncanonical package path.");
                byte[] content = Files.readAllBytes(path); expanded += content.length;
                if (expanded > MAX_BYTES || paths.size() > 9999) throw new IOException("Package exceeds supported bounds; reduce the accepted scope.");
                JsonObject member = new JsonObject(); member.addProperty("path", name); member.addProperty("size", content.length);
                member.addProperty("sha256", hash(content)); member.addProperty("kind", name.startsWith("items/") ? "item"
                        : name.startsWith("attachments/") ? "attachment" : name.substring(0, name.length() - 5));
                inventory.add(member);
            }
            manifest.add("members", inventory);
            byte[] manifestBytes = JSON.toJson(manifest).getBytes(StandardCharsets.UTF_8);
            Path partial = work.resolve("package.partial");
            try (var zip = new ZipOutputStream(Files.newOutputStream(partial))) {
                add(zip, "manifest.json", manifestBytes);
                for (Path path : paths) add(zip, members.relativize(path).toString().replace('\\', '/'), Files.readAllBytes(path));
            }
            if (Files.size(partial) > 50L * 1024 * 1024) throw new IOException("Compressed package exceeds 50 MB; reduce the accepted scope.");
            Files.createDirectories(output.getParent()); Files.move(partial, output);
            Files.writeString(Path.of(output + ".manifest.sha256"), hash(manifestBytes) + "\n", StandardOpenOption.CREATE_NEW);
            System.out.println("Package: " + output + "\nManifest SHA-256: " + hash(manifestBytes) + "\nEvidence mode: " + mode);
        }
    }
    static void validate(JsonObject config, boolean fixture) {
        UUID.fromString(text(config, "sourceInstanceId")); OffsetDateTime.parse(text(config, "capturedAt"));
        for (String name : List.of("sourceName", "serverVersion", "query", "inventoryAcceptedBy", "freezeReference"))
            if (text(config, name).isBlank()) throw new IllegalArgumentException(name + " is required.");
        List<String> ids = strings(config.getAsJsonArray("itemIds"));
        if (ids.isEmpty() || ids.size() > 5000 || new HashSet<>(ids).size() != ids.size() || ids.stream().anyMatch(x -> !x.matches("[1-9][0-9]{0,18}")))
            throw new IllegalArgumentException("Supply a bounded, distinct inventory of live decimal item IDs.");
        if (!fixture && (!config.has("hostname") || !config.has("username"))) throw new IllegalArgumentException("Connection details are required.");
    }
    static boolean qualified(JsonObject config) throws Exception {
        if (!config.has("qualification") || !config.get("qualification").isJsonObject()) return false;
        JsonObject q = config.getAsJsonObject("qualification");
        return q.has("serverVersion") && text(q, "serverVersion").equals(text(config, "serverVersion"))
                && q.has("sourceInstanceId") && text(q, "sourceInstanceId").equals(text(config, "sourceInstanceId"))
                && q.has("evidenceReference") && !text(q, "evidenceReference").isBlank()
                && q.has("acceptedBy") && !text(q, "acceptedBy").isBlank()
                && q.has("capabilityMatrixReference") && !text(q, "capabilityMatrixReference").isBlank()
                && q.has("profileSha256") && text(q, "profileSha256").equals(profileHash(config))
                && q.has("sdkSha256") && text(q, "sdkSha256").equals(sdkHash())
                // This adapter has no historical-byte decoder yet. No loss may be silently waived.
                && q.has("historicalAttachmentsInventoriedAbsent") && q.get("historicalAttachmentsInventoriedAbsent").getAsBoolean();
    }
    static String profileHash(JsonObject config) throws Exception {
        return hash(JSON.toJson(config.get("profile")).getBytes(StandardCharsets.UTF_8));
    }
    static String sdkHash() throws Exception {
        Path sdk = Path.of(com.mks.api.IntegrationPointFactory.class.getProtectionDomain().getCodeSource().getLocation().toURI());
        if (!Files.isRegularFile(sdk)) throw new IOException("Use a qualified SDK jar, not loose SDK classes.");
        return hash(Files.readAllBytes(sdk));
    }
    static void checkInventory(List<String> expected, List<String> observed) throws IOException {
        if (new HashSet<>(observed).size() != observed.size() || !new HashSet<>(expected).equals(new HashSet<>(observed)))
            throw new IOException("Enumeration differs from the administrator-accepted inventory; completeness is unproven.");
    }
    static void verifyAttachments(JsonObject item, Path members) throws Exception {
        for (JsonElement e : item.getAsJsonArray("attachments")) {
            String name = text(e.getAsJsonObject(), "path");
            if (!name.matches("attachments/[0-9a-f]{64}")) throw new IOException("Invalid attachment checkpoint path.");
            Path path = members.resolve(name);
            if (!Files.isRegularFile(path, LinkOption.NOFOLLOW_LINKS) || !hash(Files.readAllBytes(path)).equals(name.substring(12))) throw new IOException("Attachment checkpoint failed verification.");
        }
    }
    static void writeStable(Path path, String value) throws IOException {
        Files.createDirectories(path.getParent());
        if (Files.exists(path)) { if (!Files.readString(path).equals(value)) throw new IOException("Source/checkpoint changed: " + path.getFileName()); }
        else Files.writeString(path, value, StandardOpenOption.CREATE_NEW);
    }
    static void add(ZipOutputStream zip, String name, byte[] bytes) throws IOException {
        ZipEntry entry = new ZipEntry(name); entry.setTime(0); zip.putNextEntry(entry); zip.write(bytes); zip.closeEntry();
    }
    static String text(JsonObject object, String key) { return object.get(key).getAsString(); }
    static List<String> strings(JsonArray values) { List<String> result = new ArrayList<>(); for (JsonElement x : values) result.add(x.getAsString()); return result; }
    static String hash(byte[] bytes) throws Exception { return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(bytes)); }
}
