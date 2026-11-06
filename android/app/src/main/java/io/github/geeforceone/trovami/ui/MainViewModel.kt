package io.github.geeforceone.trovami.ui

import android.app.Application
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
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
import io.github.geeforceone.trovami.net.PhoneItem
import io.github.geeforceone.trovami.net.PhoneKeyword
import io.github.geeforceone.trovami.nfc.TagRead
import io.github.geeforceone.trovami.nfc.TrovamiTags
import io.github.geeforceone.trovami.photo.Photos
import io.github.geeforceone.trovami.sync.SyncWorker
import io.github.geeforceone.trovami.update.Updates
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
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
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.UUID
import javax.net.ssl.SSLException

sealed interface Screen {
    data object Camera : Screen

    // An unknown tag was scanned (or a blank one just got its code): pick its location. setup: it
    // came from Set up tags, and goes back there afterwards.
    data class LinkTag(val code: String, val written: Boolean, val setup: Boolean = false) : Screen

    // Aim the camera at a location without a tag.
    data object PickTarget : Screen

    // Tags one after another, without the camera (some phones read no tags while it's open).
    data object SetUpTags : Screen

    // back: where the scan returns to, which also decides what a scanned label does.
    data class ScanQr(val back: Screen = Camera) : Screen

    // ----- Find mode: search, look into a location, rename, keywords, move, delete. Online only. -----

    data object Find : Screen

    // The locations right inside a location and the items in it and below it; null: the Inbox.
    // from: where Back goes, the location browsed from or Find.
    data class Contents(val locationId: String?, val from: Screen = Find) : Screen

    data class ItemDetail(val id: String, val from: Screen) : Screen

    // Picking the item's new location, by tag, label or from the list.
    data class Move(val itemId: String, val from: Screen) : Screen

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
    // Returns the server it tries, so the form can show it if this fails.
    fun pair(raw: String): String? {
        val uri = Uri.parse(raw.trim())
        val server = uri.getQueryParameter("server")
        val code = uri.getQueryParameter("code")
        if (uri.scheme != "trovami" || uri.host != "pair" || server.isNullOrBlank() || code.isNullOrBlank()) {
            say("That isn't a Trovami pairing code. Open Profile → Connect a phone in the web app.")
            return null
        }
        pairWithCode(server, code)
        return server
    }

    // The same code typed in, for when scanning isn't an option.
    fun pairWithCode(server: String, code: String) = signInWith(server) { url ->
        app.api.pair(url, code.trim(), deviceName())
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
                say(connectionProblem(e))
            } catch (_: IllegalArgumentException) {
                say("That server address isn't valid.")
            } finally {
                busy = false
            }
        }
    }

    // The cause in plain words: each of these points to a different thing to check.
    private fun connectionProblem(e: IOException): String = when (e) {
        is UnknownHostException -> "Can't find that server. Check the address and the phone's connection."
        is SocketTimeoutException -> "That server didn't answer in time. Check the address and that the phone is on the right network."
        is ConnectException -> "That server refused the connection. Check the address and the port, and that the server is running."
        is SSLException -> "Can't connect securely to that server. Check its certificate, or whether the address should start with http:// instead."
        else -> "Can't reach that server. Check the address and that the phone is on the right network."
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
        leaveFind()
    }

    private fun deviceName(): String {
        val maker = Build.MANUFACTURER.replaceFirstChar { it.uppercase() }
        return if (Build.MODEL.startsWith(maker, ignoreCase = true)) Build.MODEL else "$maker ${Build.MODEL}"
    }

    // ----- Tags and labels -----

    // A trovami:// link from an NFC tag, a QR label or the pairing code. external: it opened the app
    // (or brought it back), which always means adding, as when the app was closed.
    fun openUri(raw: String, external: Boolean = false) {
        if (external && finding) leaveFind()
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
        (screen as? Screen.Move)?.let { move ->
            val location = tag?.let { dao.location(it.locationId) }
            if (location == null) say("This tag isn't linked to a location yet.") else moveTo(move, location)
            return@launch
        }
        if (finding && tag != null && !setup) {
            if (prefs.current().householdId != tag.householdId) prefs.setHousehold(tag.householdId)
            showContents(tag.locationId)
            return@launch
        }
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
        if (finding) {
            say("Tag linked to ${location.name}")
            showContents(location.id)
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

    // ----- Find mode -----

    var finding by mutableStateOf(false)
        private set

    // The last call couldn't reach the server; find mode says so until one gets through.
    var offline by mutableStateOf(false)
        private set

    var query by mutableStateOf("")
        private set
    var results by mutableStateOf<List<PhoneItem>>(emptyList())
        private set
    var searching by mutableStateOf(false)
        private set
    private var searchJob: Job? = null

    // The open location's items (null while they load), and the open item.
    var contents by mutableStateOf<List<PhoneItem>?>(null)
        private set
    var item by mutableStateOf<PhoneItem?>(null)
        private set
    var itemBusy by mutableStateOf(false)
        private set
    var keywords by mutableStateOf<List<PhoneKeyword>>(emptyList())
        private set

    fun enterFind() {
        finding = true
        cameraPaused = true
        screen = Screen.Find
        viewModelScope.launch { loadKeywords() }
    }

    fun leaveFind() {
        finding = false
        searchJob?.cancel()
        query = ""
        results = emptyList()
        contents = null
        item = null
        offline = false
        photoCache.evictAll()
        cameraPaused = false
        screen = Screen.Camera
    }

    // From a location's contents straight to adding there.
    fun addHere(location: LocationRow) {
        leaveFind()
        aimAt(location)
    }

    // Where Back goes from each screen.
    fun back() {
        screen = when (val s = screen) {
            is Screen.LinkTag -> if (s.setup) Screen.SetUpTags else home()
            is Screen.ScanQr -> s.back
            is Screen.ItemDetail -> s.from
            is Screen.Move -> s.from
            is Screen.Contents -> {
                (s.from as? Screen.Contents)?.let(::openContents) ?: run { screen = Screen.Find }
                return
            }
            Screen.Find -> {
                leaveFind()
                return
            }
            else -> home()
        }
    }

    private fun home(): Screen = if (finding) Screen.Find else Screen.Camera

    // Searches as the user types, a moment after the last key.
    fun updateQuery(text: String) {
        query = text
        searchJob?.cancel()
        if (text.isBlank()) {
            results = emptyList()
            searching = false
            return
        }
        searchJob = viewModelScope.launch {
            delay(300)
            runSearch()
        }
    }

    private suspend fun runSearch() {
        val household = prefState.value?.householdId ?: return
        val text = query.trim()
        if (text.isEmpty()) return
        searching = true
        try {
            online { app.api.search(it, household, text) }?.let { if (query.trim() == text) results = it }
        } finally {
            searching = false
        }
    }

    // Browsing from one location into another keeps the way back; anything else goes back to Find.
    fun showContents(locationId: String?) = openContents(Screen.Contents(locationId, from = screen as? Screen.Contents ?: Screen.Find))

    private fun openContents(target: Screen.Contents) {
        contents = null
        screen = target
        viewModelScope.launch { loadContents(target.locationId) }
    }

    private suspend fun loadContents(locationId: String?) {
        val household = prefState.value?.householdId ?: return
        val loaded = online { if (locationId == null) app.api.inbox(it, household) else app.api.itemsBelow(it, household, locationId) }
        if (contentsScreen()?.locationId == locationId) contents = loaded ?: contents ?: emptyList()
    }

    // After "no connection": everything on the screen again.
    fun retry() = viewModelScope.launch {
        when (val s = screen) {
            is Screen.Contents -> loadContents(s.locationId)
            is Screen.ItemDetail -> online { app.api.item(it, s.id) }?.let(::changed)
            else -> runSearch()
        }
        loadKeywords()
    }

    fun openItem(row: PhoneItem) {
        item = row
        screen = Screen.ItemDetail(row.id, screen)
        viewModelScope.launch { online { app.api.item(it, row.id) }?.let(::changed) }
    }

    // A typed name is a person's name: the AI leaves it alone. Empty: an unnamed item.
    fun rename(name: String) = edit { session, current -> app.api.rename(session, current.id, name.trim().ifEmpty { null }) }

    fun addKeyword(word: String) {
        val clean = word.trim().lowercase().split(Regex("\\s+")).joinToString(" ")
        val current = item ?: return
        if (clean.isEmpty() || clean in current.keywords) return
        edit { session, it -> app.api.setKeywords(session, it.id, it.keywords + clean) }
    }

    fun removeKeyword(word: String) = edit { session, it -> app.api.setKeywords(session, it.id, it.keywords - word) }

    fun startMove() {
        val current = item ?: return
        screen = Screen.Move(current.id, screen)
    }

    // location null: to the Inbox.
    fun moveTo(move: Screen.Move, location: LocationRow?) {
        if (location?.pending == true) {
            SyncWorker.request(app)
            say("${location.name} isn't on the server yet. Try again once the phone has synced.")
            return
        }
        if (itemBusy) return
        viewModelScope.launch {
            itemBusy = true
            try {
                val moved = online { app.api.move(it, move.itemId, location?.id) } ?: return@launch
                changed(moved)
                screen = move.from
                say("Moved to ${location?.name ?: "the Inbox"}")
                contentsScreen()?.let { loadContents(it.locationId) }
            } finally {
                itemBusy = false
            }
        }
    }

    fun deleteItem() {
        val current = item ?: return
        if (itemBusy) return
        viewModelScope.launch {
            itemBusy = true
            try {
                online { app.api.delete(it, current.id) } ?: return@launch
                results = results.filterNot { it.id == current.id }
                contents = contents?.filterNot { it.id == current.id }
                item = null
                screen = (screen as? Screen.ItemDetail)?.from ?: home()
                say("Deleted ${current.shownName}")
            } finally {
                itemBusy = false
            }
        }
    }

    // The location list on screen, or the one the open item was opened from.
    private fun contentsScreen(): Screen.Contents? = when (val s = screen) {
        is Screen.Contents -> s
        is Screen.ItemDetail -> s.from as? Screen.Contents
        else -> null
    }

    private fun edit(call: suspend (Session, PhoneItem) -> PhoneItem) {
        val current = item ?: return
        if (itemBusy) return
        viewModelScope.launch {
            itemBusy = true
            try {
                online { call(it, current) }?.let {
                    changed(it)
                    loadKeywords()
                }
            } finally {
                itemBusy = false
            }
        }
    }

    // The item as the server has it now, wherever it's shown.
    private fun changed(updated: PhoneItem) {
        if (item?.id == updated.id) item = updated
        results = results.map { if (it.id == updated.id) updated else it }
        contents = contents?.map { if (it.id == updated.id) updated else it }
    }

    private suspend fun loadKeywords() {
        val household = prefState.value?.householdId ?: return
        online(quiet = true) { app.api.keywords(it, household) }?.let { keywords = it }
    }

    // Runs a find-mode call. Null when it failed: a server's refusal is said, no connection shows as offline.
    private suspend fun <T> online(quiet: Boolean = false, call: suspend (Session) -> T): T? {
        val session = prefs.session() ?: return null
        return try {
            call(session).also { offline = false }
        } catch (e: ApiException) {
            offline = false
            if (!quiet) say(e.message ?: "The server answered with an error.")
            null
        } catch (e: IOException) {
            Log.w(TAG, "Find mode call failed", e)
            offline = true
            null
        }
    }

    // Photos for find mode, by item, version and size, kept while the mode is open.
    private val photoCache = object : LruCache<String, ImageBitmap>(48 * 1024 * 1024) {
        override fun sizeOf(key: String, value: ImageBitmap) = value.width * value.height * 4
    }

    fun cachedPhoto(row: PhoneItem, thumb: Boolean): ImageBitmap? = photoCache.get(photoKey(row, thumb))

    suspend fun loadPhoto(row: PhoneItem, thumb: Boolean): ImageBitmap? {
        if (row.photoVersion == null) return null
        val session = prefs.session() ?: return null
        val key = photoKey(row, thumb)
        photoCache.get(key)?.let { return it }
        return try {
            val bytes = app.api.photo(session, row.id, thumb)
            withContext(Dispatchers.Default) { BitmapFactory.decodeByteArray(bytes, 0, bytes.size)?.asImageBitmap() }
                ?.also { photoCache.put(key, it) }
        } catch (_: IOException) {
            null
        }
    }

    private fun photoKey(row: PhoneItem, thumb: Boolean) = "${row.id}:${row.photoVersion}:$thumb"

    // Here, after the fields it uses: another household means what find mode shows belongs to the old one.
    init {
        viewModelScope.launch {
            householdId.collect {
                photoCache.evictAll()
                if (finding) {
                    contents = null
                    loadKeywords()
                    if (screen is Screen.Contents || screen is Screen.ItemDetail || screen is Screen.Move) screen = Screen.Find
                    if (query.isNotBlank()) runSearch()
                }
            }
        }
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
