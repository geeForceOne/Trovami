package io.github.geeforceone.trovami.ui

import android.app.Application
import android.graphics.Bitmap
import android.net.Uri
import android.nfc.Tag
import android.os.Build
import android.util.Log
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import io.github.geeforceone.trovami.BuildConfig
import io.github.geeforceone.trovami.TrovamiApp
import io.github.geeforceone.trovami.data.ItemRow
import io.github.geeforceone.trovami.data.LocationRow
import io.github.geeforceone.trovami.data.PrefState
import io.github.geeforceone.trovami.data.Session
import io.github.geeforceone.trovami.data.TagRow
import io.github.geeforceone.trovami.net.Api
import io.github.geeforceone.trovami.net.ApiException
import io.github.geeforceone.trovami.net.AppRelease
import io.github.geeforceone.trovami.nfc.TagRead
import io.github.geeforceone.trovami.nfc.TrovamiTags
import io.github.geeforceone.trovami.photo.Photos
import io.github.geeforceone.trovami.sync.SyncWorker
import io.github.geeforceone.trovami.update.Updates
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.flatMapLatest
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.IOException
import java.util.UUID

sealed interface Screen {
    data object Camera : Screen

    // An unknown tag was scanned (or a blank one just got its code): pick its location. setup: it
    // came from Set up tags, and goes back there afterwards.
    data class LinkTag(val code: String, val written: Boolean, val setup: Boolean = false) : Screen

    // Aim the camera at a location without a tag.
    data object PickTarget : Screen

    // Tags one after another, without the camera (some phones read no tags while it's open).
    data object SetUpTags : Screen

    data object ScanQr : Screen

    data object Settings : Screen
}

private const val TAG = "Trovami"

// What the user typed for the next photo; cleared after each shot.
data class Details(val name: String = "", val notes: String = "", val quantity: Int = 1)

@OptIn(ExperimentalCoroutinesApi::class)
class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as TrovamiApp
    private val dao = app.db.dao()
    private val prefs = app.prefs

    // Null until the stored settings are read.
    val prefState: StateFlow<PrefState?> = prefs.state.stateIn(viewModelScope, SharingStarted.Eagerly, null)

    val households = dao.households().stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())

    private val householdId = prefState.map { it?.householdId }.distinctUntilChanged()

    val locations: StateFlow<List<LocationRow>> = householdId
        .flatMapLatest { if (it == null) flowOf(emptyList()) else dao.locations(it) }
        .stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())

    // Where new photos go: a location of the current household, or its Inbox (null). A location
    // that's deleted meanwhile falls back to the Inbox.
    private val targetId = MutableStateFlow<String?>(null)
    val target: StateFlow<LocationRow?> = combine(targetId, locations) { id, all -> all.firstOrNull { it.id == id } }
        .stateIn(viewModelScope, SharingStarted.Eagerly, null)

    val waiting = dao.waitingCount().stateIn(viewModelScope, SharingStarted.Eagerly, 0)

    var screen by mutableStateOf<Screen>(Screen.Camera)

    // The camera closes when it's idle, so the phone can read tags again; a tap or a tag opens it.
    var cameraPaused by mutableStateOf(false)

    // On Set up tags: the location the last tag was linked to, or the one a scanned tag already has.
    var setUpLinked by mutableStateOf<String?>(null)
        private set
    var setUpFound by mutableStateOf<Pair<String, LocationRow?>?>(null)
        private set

    var details by mutableStateOf(Details())
    var busy by mutableStateOf(false)
        private set

    // A newer release than this app, and the download's progress (0..1) while it runs.
    var update by mutableStateOf<AppRelease?>(null)
        private set
    var downloading by mutableStateOf<Float?>(null)
        private set

    // Shown once after the app was updated.
    var updatedTo by mutableStateOf<AppRelease?>(null)
        private set

    private val messageChannel = Channel<String>(Channel.BUFFERED)
    val messages = messageChannel.receiveAsFlow()

    init {
        viewModelScope.launch {
            if (prefs.session() != null) {
                SyncWorker.request(app)
                checkForUpdate(force = false)
            }
        }
    }

    fun say(message: String) {
        messageChannel.trySend(message)
    }

    // ----- Sign-in -----

    suspend fun lastServer(): String? = prefs.lastServer()

    fun signIn(server: String, email: String, password: String) = signInWith(server) { url ->
        app.api.signIn(url, email.trim(), password, deviceName())
    }

    // The pairing QR from the web app's Profile: trovami://pair?server=…&code=…
    fun pair(raw: String) {
        val uri = Uri.parse(raw.trim())
        val server = uri.getQueryParameter("server")
        val code = uri.getQueryParameter("code")
        if (uri.scheme != "trovami" || uri.host != "pair" || server.isNullOrBlank() || code.isNullOrBlank()) {
            say("That isn't a Trovami pairing code. Open Profile → Connect a phone in the web app.")
            return
        }
        signInWith(server) { url -> app.api.pair(url, code, deviceName()) }
    }

    private fun signInWith(server: String, call: suspend (String) -> io.github.geeforceone.trovami.net.SignInResponse) {
        if (busy) return
        viewModelScope.launch {
            busy = true
            try {
                val url = Api.normalizeServer(server)
                val response = call(url)
                prefs.signIn(Session(url, response.token, response.name, response.email))
                targetId.value = null
                screen = Screen.Camera
                SyncWorker.request(app)
                say("Signed in as ${response.name}.")
                checkForUpdate(force = true, announce = false)
            } catch (e: ApiException) {
                say(e.message ?: "Signing in failed.")
            } catch (e: IOException) {
                Log.w(TAG, "Signing in failed", e)
                say("Can't reach that server. Check the address and that the phone is on the right network.")
            } catch (_: IllegalArgumentException) {
                say("That server address isn't valid.")
            } finally {
                busy = false
            }
        }
    }

    // Unsent items are lost; the screen warns about them first.
    fun signOut() = viewModelScope.launch {
        val session = prefs.session()
        if (session != null) runCatching { app.api.signOut(session) }
        prefs.signOut()
        withContext(Dispatchers.IO) {
            app.db.clearAllTables()
            Photos.deleteAll(app)
        }
        targetId.value = null
        screen = Screen.Camera
    }

    private fun deviceName(): String {
        val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
        return if (Build.MODEL.startsWith(maker, ignoreCase = true)) Build.MODEL else "$maker ${Build.MODEL}"
    }

    // ----- Tags and labels -----

    // A trovami:// link from an NFC tag, a QR label or the pairing code.
    fun openUri(raw: String) {
        if (raw.startsWith("trovami://pair", ignoreCase = true)) {
            viewModelScope.launch {
                if (prefs.session() == null) pair(raw) else say("This phone is already signed in.")
            }
            return
        }
        val code = TrovamiTags.codeOf(raw)
        if (code == null) say("That isn't a Trovami tag or label.") else openCode(code)
    }

    private fun openCode(code: String) = viewModelScope.launch {
        if (prefs.session() == null) {
            say("Sign in first, then scan the tag again.")
            return@launch
        }
        val setup = screen == Screen.SetUpTags
        val tag = dao.tag(code)
        if (tag == null) {
            screen = Screen.LinkTag(code, written = false, setup = setup)
            return@launch
        }
        if (setup) {
            setUpLinked = null
            setUpFound = code to dao.location(tag.locationId)
            return@launch
        }
        aim(tag.householdId, tag.locationId)
        cameraPaused = false
        screen = Screen.Camera
        dao.location(tag.locationId)?.let { say("Adding to ${it.name}") }
    }

    // Called by NFC reader mode, off the main thread, while the tag is at the phone.
    fun onNfcTag(tag: Tag) {
        when (val read = TrovamiTags.read(tag)) {
            is TagRead.Trovami -> viewModelScope.launch { openCode(read.code) }
            TagRead.Other -> say("This tag already holds something else. Use a blank tag.")
            TagRead.Blank -> {
                if (prefState.value?.session == null) {
                    say("Sign in first, then scan the tag again.")
                    return
                }
                val code = TrovamiTags.newCode()
                try {
                    TrovamiTags.write(tag, code, app.packageName)
                    viewModelScope.launch { screen = Screen.LinkTag(code, written = true, setup = screen == Screen.SetUpTags) }
                } catch (e: IOException) {
                    say(e.message?.takeIf { it.isNotBlank() } ?: "Writing the tag failed. Hold it still and try again.")
                }
            }
        }
    }

    fun link(code: String, location: LocationRow, setup: Boolean) = viewModelScope.launch {
        dao.upsertTag(TagRow(code, location.householdId, location.id, pending = true))
        SyncWorker.request(app)
        if (setup) {
            setUpFound = null
            setUpLinked = location.name
            screen = Screen.SetUpTags
            return@launch
        }
        aim(location.householdId, location.id)
        cameraPaused = false
        screen = Screen.Camera
        say("Tag linked to ${location.name}")
    }

    fun openSetUpTags() {
        setUpLinked = null
        setUpFound = null
        screen = Screen.SetUpTags
    }

    // The tag shown on Set up tags, to link it somewhere else.
    fun relinkFound() {
        val (code, _) = setUpFound ?: return
        screen = Screen.LinkTag(code, written = false, setup = true)
    }

    // A location made on the phone (while linking a tag), under parent or at the top.
    suspend fun createLocation(parent: LocationRow?, name: String, kind: String): LocationRow? {
        val household = prefState.value?.householdId ?: return null
        val row = LocationRow(UUID.randomUUID().toString(), household, parent?.id, name.trim(), kind, pending = true)
        dao.insertLocation(row)
        SyncWorker.request(app)
        return row
    }

    // ----- Adding -----

    fun aimAt(location: LocationRow?) = viewModelScope.launch {
        if (location == null) targetId.value = null else aim(location.householdId, location.id)
        cameraPaused = false
        screen = Screen.Camera
    }

    private suspend fun aim(householdId: String, locationId: String?) {
        if (prefs.current().householdId != householdId) prefs.setHousehold(householdId)
        targetId.value = locationId
    }

    fun switchHousehold(id: String) = viewModelScope.launch {
        prefs.setHousehold(id)
        targetId.value = null
    }

    // typed: the details as they were when the shutter was pressed. Called off the main thread.
    fun capture(bitmap: Bitmap, rotationDegrees: Int, typed: Details) {
        val household = prefState.value?.householdId
        if (household == null) {
            say("No household yet. Create one in the web app, then sync.")
            return
        }
        val locationId = target.value?.id
        viewModelScope.launch {
            val id = UUID.randomUUID().toString()
            withContext(Dispatchers.Default) { Photos.save(bitmap, rotationDegrees, Photos.file(app, id)) }
            bitmap.recycle()
            dao.insertItem(
                ItemRow(
                    id = id,
                    householdId = household,
                    locationId = locationId,
                    name = typed.name.trim().ifEmpty { null },
                    notes = typed.notes.trim().ifEmpty { null },
                    quantity = typed.quantity.coerceAtLeast(1),
                    hasPhoto = true,
                    createdAt = System.currentTimeMillis(),
                )
            )
            SyncWorker.request(app)
        }
    }

    fun syncNow() {
        SyncWorker.request(app)
        say("Syncing…")
    }

    // ----- Updates -----

    // At most twice a day unless forced; "dev" builds never offer updates. announce: say how it
    // went (when the user asked for the check).
    fun checkForUpdate(force: Boolean, announce: Boolean = force) = viewModelScope.launch {
        val current = BuildConfig.VERSION_NAME
        if (!Updates.isRelease(current)) {
            if (announce) say("This is a development build; it doesn't update itself.")
            return@launch
        }
        val state = prefs.current()
        val session = state.session ?: return@launch
        val seen = state.seenVersion
        val due = force || System.currentTimeMillis() - state.updateCheckedAt > 12 * 60 * 60 * 1000L
        if (!due && seen == current) return@launch
        try {
            val latest = app.api.latestRelease(session)
            prefs.setUpdateCheckedAt(System.currentTimeMillis())
            if (seen != current) {
                // Not on the first install, only after an update.
                if (seen != null) updatedTo = latest?.takeIf { it.version == current } ?: AppRelease(current)
                prefs.setSeenVersion(current)
            }
            update = latest?.takeIf { Updates.isNewer(it.version, current) && it.apkUrl != null }
            if (announce && update == null) say("Trovami is up to date.")
        } catch (_: IOException) {
            if (announce) say("Couldn't check for updates right now.")
        }
    }

    fun dismissUpdatedTo() {
        updatedTo = null
    }

    fun installUpdate() {
        val release = update ?: return
        val url = release.apkUrl ?: return
        if (downloading != null) return
        viewModelScope.launch {
            downloading = 0f
            try {
                val apk = Updates.apkFile(app)
                app.api.download(url, apk) { downloading = it }
                Updates.install(app, apk)
            } catch (_: IOException) {
                say("The download failed. Try again later.")
            } finally {
                downloading = null
            }
        }
    }
}
