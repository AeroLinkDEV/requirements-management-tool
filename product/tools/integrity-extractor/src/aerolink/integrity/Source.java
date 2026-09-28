package aerolink.integrity;

import com.google.gson.*;
import java.nio.file.Path;
import java.util.List;

/** The read-only extraction boundary, shared by the real SDK adapter and explicitly labelled fixtures. */
public interface Source extends AutoCloseable {
    List<String> enumerate() throws Exception;
    JsonObject schema() throws Exception;
    JsonArray users() throws Exception;
    JsonObject item(String id, Path attachmentDirectory) throws Exception;
    @Override void close() throws Exception;
}
