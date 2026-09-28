package aerolink.integrity;

import com.google.gson.*;
import com.mks.api.*;
import com.mks.api.response.*;
import java.nio.file.*;
import java.util.*;

/** Only read commands are constructed here. A customer-qualified profile names server-specific response fields. */
final class MksSource implements Source {
    private final JsonObject config;
    private final IntegrationPoint point;
    private final Session session;
    private final CmdRunner runner;
    MksSource(JsonObject config) throws Exception {
        this.config = config;
        String password = System.getenv("AEROLINK_INTEGRITY_PASSWORD");
        if (password == null || password.isBlank()) throw new IllegalArgumentException("Set AEROLINK_INTEGRITY_PASSWORD in the extractor process environment.");
        point = IntegrationPointFactory.getInstance().createIntegrationPoint(Extractor.text(config, "hostname"),
                config.get("port").getAsInt(), true, config.get("apiMajor").getAsInt(), config.get("apiMinor").getAsInt());
        session = point.createNamedSession("AeroLinkIntegrityExtractor-1", null, Extractor.text(config, "username"), password);
        session.setTimeout(300_000); session.setAutoReconnect(false);
        runner = session.createCmdRunner();
    }
    private Response execute(String name, Map<String, String> options, List<String> selections) throws Exception {
        if (!Set.of("issues", "viewissue", "fields", "types", "users", "extractattachments").contains(name)) throw new IllegalArgumentException("Only extraction commands are allowed.");
        Command command = new Command(Command.IM, name);
        for (var option : options.entrySet()) command.addOption(option.getValue() == null ? new Option(option.getKey()) : new Option(option.getKey(), option.getValue()));
        for (String selection : selections) command.addSelection(selection);
        Response response = runner.execute(command);
        if (response.getExitCode() != 0 || response.getAPIException() != null) { response.release(); throw new IllegalStateException("Integrity refused a read command; capture is incomplete."); }
        return response;
    }
    public List<String> enumerate() throws Exception {
        Response response = execute("issues", Map.of("queryDefinition", Extractor.text(config, "query"), "fields", "ID"), List.of());
        try {
            List<String> ids = new ArrayList<>(); WorkItemIterator iterator = response.getWorkItems();
            while (iterator.hasNext()) { WorkItem item = iterator.next(); requireItem(item); ids.add(item.getId()); }
            return ids;
        } finally { response.release(); }
    }
    public JsonObject schema() throws Exception {
        JsonObject result = new JsonObject(); result.add("fields", metadata("fields")); result.add("types", metadata("types")); return result;
    }
    public JsonArray users() throws Exception { return metadata("users"); }
    private JsonArray metadata(String command) throws Exception {
        Response response = execute(command, Map.of(), List.of());
        try { JsonArray result = new JsonArray(); WorkItemIterator items = response.getWorkItems();
            while (items.hasNext()) { WorkItem item = items.next(); requireItem(item); result.add(encodeItem(item, 0)); } return result;
        } finally { response.release(); }
    }
    public JsonObject item(String id, Path capture) throws Exception {
        Map<String, String> flags = new LinkedHashMap<>();
        for (String flag : List.of("showHistory", "showHistoryWithIndirectEdits", "showHistoryWithComputedField", "showAnnotations",
                "showAttachmentDetails", "showRelationships", "showRichContent", "showXHTML", "showSourceLinkDetails", "showSourceTraceDetails",
                "showBranches", "showTimeEntries", "showIncomingExternalReferences")) flags.put(flag, null);
        Response response = execute("viewissue", flags, List.of(id));
        try {
            WorkItem item = response.getWorkItem(id); requireItem(item);
            JsonObject profile = config.getAsJsonObject("profile");
            JsonObject result = new JsonObject(); result.addProperty("id", item.getId());
            result.addProperty("project", requiredField(item, Extractor.text(profile, "projectField")).getValueAsString());
            result.addProperty("type", requiredField(item, Extractor.text(profile, "typeField")).getValueAsString());
            result.addProperty("snapshot", Extractor.text(config, "capturedAt"));
            result.add("fields", encodeFields(item, 0, profile));
            result.add("history", requiredArray(item, Extractor.text(profile, "historyField")));
            result.add("annotations", requiredArray(item, Extractor.text(profile, "annotationsField")));
            JsonArray relationships = new JsonArray();
            for (JsonElement name : profile.getAsJsonArray("relationshipFields")) {
                Field field = requiredField(item, name.getAsString()); JsonObject reference = new JsonObject();
                reference.addProperty("field", field.getName()); reference.add("value", encode(field.getValue(), 0)); relationships.add(reference);
            }
            result.add("relationships", relationships);
            JsonArray attachments = new JsonArray(); int ordinal = 0;
            for (JsonElement name : profile.getAsJsonArray("attachmentFields")) {
                Field field = requiredField(item, name.getAsString());
                if (!(field.getValue() instanceof List<?> list)) throw new IllegalStateException("Attachment response shape needs server qualification.");
                for (Object entry : list) {
                    if (!(entry instanceof Item attachment)) throw new IllegalStateException("Attachment metadata is not a typed item.");
                    String fileName = requiredField(attachment, Extractor.text(profile, "attachmentNameField")).getValueAsString();
                    Path target = capture.resolve("attachment-" + ordinal++);
                    if (Files.exists(target)) throw new IllegalStateException("Incomplete attachment capture exists; use a new checkpoint directory.");
                    Response extracted = execute("extractattachments", Map.of("issue", id, "field", field.getName(), "outputFile", target.toString()), List.of(fileName));
                    extracted.release();
                    JsonObject descriptor = new JsonObject(); descriptor.addProperty("field", field.getName()); descriptor.addProperty("name", fileName);
                    descriptor.addProperty("contentType", requiredField(attachment, Extractor.text(profile, "attachmentMimeField")).getValueAsString());
                    descriptor.addProperty("localFile", target.toString());
                    descriptor.addProperty("sourceAuthor", optional(attachment, profile, "attachmentAuthorField"));
                    descriptor.addProperty("sourceDate", optional(attachment, profile, "attachmentDateField"));
                    descriptor.addProperty("sourceUri", optional(attachment, profile, "attachmentUriField")); attachments.add(descriptor);
                }
            }
            result.add("attachments", attachments); result.add("findings", new JsonArray()); return result;
        } finally { response.release(); }
    }
    private static String optional(Item item, JsonObject profile, String key) {
        if (!profile.has(key) || !item.contains(Extractor.text(profile, key))) return null;
        return item.getField(Extractor.text(profile, key)).getValueAsString();
    }
    private static JsonArray requiredArray(Item item, String name) throws Exception {
        JsonElement value = encode(requiredField(item, name).getValue(), 0);
        if (!value.isJsonArray()) throw new IllegalStateException("Missing/unsupported " + name + " is not evidence of an empty history.");
        return value.getAsJsonArray();
    }
    private static Field requiredField(Item item, String name) {
        if (!item.contains(name)) throw new IllegalStateException("A qualified response field is absent: " + name);
        return item.getField(name);
    }
    private static void requireItem(WorkItem item) throws Exception {
        if (item == null || item.getAPIException() != null) throw new IllegalStateException("Item retrieval was incomplete.");
    }
    private static JsonObject encodeItem(Item item, int depth) throws Exception {
        JsonObject result = new JsonObject(); result.addProperty("id", item.getId()); result.addProperty("modelType", item.getModelType());
        result.add("fields", encodeFields(item, depth + 1, null)); return result;
    }
    private static JsonArray encodeFields(Item item, int depth, JsonObject profile) throws Exception {
        JsonArray fields = new JsonArray(); Iterator<?> iterator = item.getFields();
        while (iterator.hasNext()) {
            Field field = (Field)iterator.next(); JsonObject value = new JsonObject();
            value.addProperty("name", field.getName()); value.addProperty("type", field.getDataType());
            value.add("value", encode(field.getValue(), depth + 1));
            // java.util.Date cannot preserve the original zone or distinguish a date-only source field.
            // Preserve both SDK representations; only a server-qualified semantic decoder may interpret one.
            if (field.getValue() instanceof Date) value.addProperty("sourceDisplay", field.getValueAsString());
            String representation = profile != null && Extractor.strings(profile.getAsJsonArray("richTextFields")).contains(field.getName())
                    ? "entity-protected-xhtml" : "text";
            value.addProperty("representation", representation); fields.add(value);
        }
        return fields;
    }
    private static JsonElement encode(Object value, int depth) throws Exception {
        if (depth > 24) throw new IllegalArgumentException("API response nesting exceeds the capture limit.");
        if (value == null) return JsonNull.INSTANCE;
        if (value instanceof String s) return new JsonPrimitive(s);
        if (value instanceof Boolean b) return new JsonPrimitive(b);
        if (value instanceof Number n) return new JsonPrimitive(n);
        if (value instanceof Date date) {
            JsonObject retained = new JsonObject(); retained.addProperty("sdkType", "java.util.Date");
            retained.addProperty("epochMilliseconds", date.getTime()); return retained;
        }
        if (value instanceof byte[] bytes) return new JsonPrimitive(Base64.getEncoder().encodeToString(bytes));
        if (value instanceof Item item) return encodeItem(item, depth + 1);
        if (value instanceof List<?> list) { JsonArray array = new JsonArray(); for (Object entry : list) array.add(encode(entry, depth + 1)); return array; }
        throw new IllegalArgumentException("Unsupported API value type: " + value.getClass().getName());
    }
    public void close() throws Exception { try { runner.release(); } finally { try { session.release(); } finally { point.release(); } } }
}
