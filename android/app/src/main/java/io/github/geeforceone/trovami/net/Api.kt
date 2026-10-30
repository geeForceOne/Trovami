package io.github.geeforceone.trovami.net

import io.github.geeforceone.trovami.data.Session
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

// The server's phone API (/api/v1, see PhoneApi.cs). v1 only ever gains fields, so unknown ones
// are ignored.

@Serializable
data class SignInResponse(val token: String, val name: String, val email: String? = null)

@Serializable
data class SyncHousehold(val id: String, val name: String, val isOwner: Boolean)

@Serializable
data class SyncLocation(
    val id: String,
    val householdId: String,
    val parentId: String? = null,
    val name: String,
    val kind: String,
    val color: String? = null,
    val label: String? = null,
    val deleted: Boolean = false,
)

@Serializable
data class SyncTag(val code: String, val householdId: String, val locationId: String, val deleted: Boolean = false)

@Serializable
data class SyncPull(val cursor: Long, val households: List<SyncHousehold>, val locations: List<SyncLocation>, val tags: List<SyncTag>)

@Serializable
data class PushLocation(val id: String, val householdId: String, val parentId: String?, val name: String, val kind: String)

@Serializable
data class PushTag(val code: String, val locationId: String)

@Serializable
data class PushItem(
    val id: String,
    val householdId: String,
    val locationId: String?,
    val name: String?,
    val notes: String?,
    val quantity: Int,
    val hasPhoto: Boolean,
)

@Serializable
data class SyncPush(val locations: List<PushLocation>, val tags: List<PushTag>, val items: List<PushItem>)

// Ok is also true when the server already had it; a refused entry won't succeed on a retry.
@Serializable
data class PushResult(val id: String, val ok: Boolean, val error: String? = null)

@Serializable
data class SyncPushResult(val locations: List<PushResult>, val tags: List<PushResult>, val items: List<PushResult>)

@Serializable
data class AppRelease(val version: String, val apkUrl: String? = null, val notes: String? = null, val pageUrl: String = "")

@Serializable
private data class PairRequest(val code: String, val deviceName: String, val appVersion: String)

@Serializable
private data class SignInRequest(val email: String, val password: String, val deviceName: String, val appVersion: String)

@Serializable
private data class PullRequest(val cursor: Long, val households: List<String>)

// The server answered with an error; message is the server's own text when it sent one.
class ApiException(val status: Int, message: String) : IOException(message)

class Api(private val appVersion: String) {
    private val http = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .writeTimeout(120, TimeUnit.SECONDS)
        .build()

    val json = Json { ignoreUnknownKeys = true; encodeDefaults = true }
    private val jsonType = "application/json".toMediaType()

    suspend fun pair(server: String, code: String, deviceName: String): SignInResponse =
        post(server, null, "pair", json.encodeToString(PairRequest(code, deviceName, appVersion))).let(json::decodeFromString)

    suspend fun signIn(server: String, email: String, password: String, deviceName: String): SignInResponse =
        post(server, null, "signin", json.encodeToString(SignInRequest(email, password, deviceName, appVersion))).let(json::decodeFromString)

    suspend fun signOut(session: Session) {
        post(session.server, session.token, "signout", "{}")
    }

    suspend fun pull(session: Session, cursor: Long, households: List<String>): SyncPull =
        post(session.server, session.token, "sync/pull", json.encodeToString(PullRequest(cursor, households))).let(json::decodeFromString)

    suspend fun push(session: Session, push: SyncPush): SyncPushResult =
        post(session.server, session.token, "sync/push", json.encodeToString(push)).let(json::decodeFromString)

    suspend fun uploadPhoto(session: Session, itemId: String, photo: File) {
        send(request(session.server, session.token, "items/$itemId/photo").put(photo.asRequestBody("image/jpeg".toMediaType())).build())
    }

    // Null when the server doesn't know a release (yet).
    suspend fun latestRelease(session: Session): AppRelease? {
        val body = send(request(session.server, session.token, "app/latest").get().build())
        return if (body.isBlank()) null else json.decodeFromString(body)
    }

    // A file from anywhere (the APK on GitHub), written to target.
    suspend fun download(url: String, target: File, progress: (Float) -> Unit) = withContext(Dispatchers.IO) {
        http.newCall(Request.Builder().url(url).build()).execute().use { response ->
            if (!response.isSuccessful) throw ApiException(response.code, "The download failed (${response.code}).")
            val total = response.body.contentLength()
            target.parentFile?.mkdirs()
            target.outputStream().use { out ->
                response.body.byteStream().use { input ->
                    val buffer = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        out.write(buffer, 0, read)
                        done += read
                        if (total > 0) progress(done.toFloat() / total)
                    }
                }
            }
        }
    }

    private suspend fun post(server: String, token: String?, path: String, body: String): String =
        send(request(server, token, path).post(body.toRequestBody(jsonType)).build())

    private fun request(server: String, token: String?, path: String): Request.Builder =
        Request.Builder().url("${server.trimEnd('/')}/api/v1/$path").header("X-Trovami-Version", appVersion).apply {
            if (token != null) header("Authorization", "Bearer $token")
        }

    private suspend fun send(request: Request): String = withContext(Dispatchers.IO) {
        http.newCall(request).execute().use { response ->
            val text = response.body.string()
            if (!response.isSuccessful) throw ApiException(response.code, problemOf(text) ?: defaultMessage(response.code))
            text
        }
    }

    // ASP.NET Core's problem details carry the readable reason in "detail".
    private fun problemOf(text: String): String? = runCatching {
        json.parseToJsonElement(text).jsonObject["detail"]?.jsonPrimitive?.content
    }.getOrNull()

    private fun defaultMessage(status: Int) = when (status) {
        401 -> "This phone is no longer signed in."
        404 -> "That isn't a Trovami server, or it's too old for this app."
        else -> "The server answered with an error ($status)."
    }

    companion object {
        // What the user typed, made into a base URL. A home server is usually plain http.
        fun normalizeServer(input: String): String {
            val trimmed = input.trim().trimEnd('/')
            return if (trimmed.startsWith("http://", true) || trimmed.startsWith("https://", true)) trimmed else "http://$trimmed"
        }
    }
}
