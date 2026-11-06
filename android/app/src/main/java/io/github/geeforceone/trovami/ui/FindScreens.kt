package io.github.geeforceone.trovami.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.AddAPhoto
import androidx.compose.material.icons.outlined.Check
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.CloudOff
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Image
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.outlined.PhotoCamera
import androidx.compose.material.icons.outlined.Place
import androidx.compose.material.icons.outlined.QrCodeScanner
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.InputChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SuggestionChip
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.geeforceone.trovami.data.LocationRow
import io.github.geeforceone.trovami.net.PhoneItem

// Find mode: the other half of the app, for things already stored. Search, look into a location (by
// scanning its tag or label), rename, keywords, move and delete. It talks to the server directly, so it
// needs a connection; adding stays offline-first.

@Composable
fun FindScreen(model: MainViewModel) {
    val locations by model.locations.collectAsStateWithLifecycle()
    val query = model.query
    // Locations come from the phone's own copy, the same match as the location picker's.
    val locationHits = remember(locations, query) {
        val q = query.trim()
        if (q.isEmpty()) emptyList()
        else locations.filter { it.name.contains(q, ignoreCase = true) || it.label?.contains(q, ignoreCase = true) == true }
            .sortedBy { pathOf(it, locations).lowercase() }
            .take(20)
    }
    val topLevel = remember(locations) { childrenOf(null, locations) }
    val focus = remember { FocusRequester() }
    val focusManager = LocalFocusManager.current
    LaunchedEffect(Unit) { if (model.query.isEmpty()) focus.requestFocus() }

    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().imePadding()) {
        Row(Modifier.padding(start = 4.dp, end = 8.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = model::leaveFind) { Icon(Icons.Outlined.PhotoCamera, contentDescription = "Add things") }
            Text("Find", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            IconButton(onClick = { model.screen = Screen.ScanQr(back = Screen.Find) }) {
                Icon(Icons.Outlined.QrCodeScanner, contentDescription = "Scan a QR label")
            }
            HouseholdSwitcher(model)
        }
        OutlinedTextField(
            value = query,
            onValueChange = model::updateQuery,
            leadingIcon = { Icon(Icons.Outlined.Search, contentDescription = null) },
            trailingIcon = {
                when {
                    model.searching -> CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    query.isNotEmpty() -> IconButton(onClick = { model.updateQuery("") }) { Icon(Icons.Outlined.Close, contentDescription = "Clear") }
                }
            },
            placeholder = { Text("Find an item or location") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            // It searches as you type; the key only puts the keyboard away to show the results.
            keyboardActions = KeyboardActions(onSearch = { focusManager.clearFocus() }),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp).focusRequester(focus),
        )
        OfflineBanner(model)

        LazyColumn(Modifier.fillMaxSize()) {
            if (query.isBlank()) {
                // Browsing the tree starts at the top; the phone has it, so this works offline too.
                if (topLevel.isNotEmpty()) item(key = "locations") { Section("Locations") }
                items(topLevel, key = { "l-" + it.id }) { row ->
                    LocationLine(row, depth = 0, path = null, onPick = { model.showContents(row.id) }, onAdd = null)
                }
                item(key = "inbox") {
                    HorizontalDivider(Modifier.padding(vertical = 4.dp))
                    PickRow(onClick = { model.showContents(null) }) {
                        Icon(Icons.Outlined.Inbox, contentDescription = null, tint = MaterialTheme.colorScheme.secondary)
                        Spacer(Modifier.width(16.dp))
                        Text("Inbox", style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
                    }
                }
                item(key = "hint") { Hint("Hold a tag to the phone, or scan a label, to see what's in that location.") }
                return@LazyColumn
            }
            if (locationHits.isNotEmpty()) {
                item(key = "locations") { Section("Locations") }
                items(locationHits, key = { "l-" + it.id }) { row ->
                    LocationLine(row, depth = 0, path = pathOf(row, locations), onPick = { model.showContents(row.id) }, onAdd = null)
                }
            }
            item(key = "items") { Section("Items") }
            if (model.results.isEmpty() && !model.searching && !model.offline) {
                item(key = "none") { Hint("No item matches \"${query.trim()}\".") }
            }
            items(model.results, key = { "i-" + it.id }) { row ->
                ItemLine(model, row, showPath = true) { model.openItem(row) }
            }
        }
    }
}

// What's in a location: the locations right inside it to go into, then the items in it and below
// it. Or the Inbox.
@Composable
fun ContentsScreen(model: MainViewModel, screen: Screen.Contents) {
    val locations by model.locations.collectAsStateWithLifecycle()
    val location = locations.firstOrNull { it.id == screen.locationId }
    val parent = location?.parentId?.let { id -> locations.firstOrNull { it.id == id } }
    val inside = remember(locations, screen.locationId) { if (screen.locationId == null) emptyList() else childrenOf(screen.locationId, locations) }

    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Row(Modifier.padding(start = 4.dp, end = 8.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = model::back) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back") }
            Column(Modifier.weight(1f)) {
                Text(
                    if (screen.locationId == null) "Inbox" else location?.name ?: "Location",
                    style = MaterialTheme.typography.titleLarge,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                // The path up to here; tapping it goes up one level.
                parent?.let {
                    Text(pathOf(it, locations), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1, overflow = TextOverflow.StartEllipsis,
                        modifier = Modifier.clickable(onClickLabel = "Go to ${it.name}") { model.showContents(it.id) })
                }
            }
            if (location != null) {
                IconButton(onClick = { model.addHere(location) }) { Icon(Icons.Outlined.AddAPhoto, contentDescription = "Add things here") }
            }
        }
        OfflineBanner(model)
        val items = model.contents
        LazyColumn(Modifier.fillMaxSize()) {
            if (inside.isNotEmpty()) {
                item(key = "locations") { Section("Locations") }
                items(inside, key = { "l-" + it.id }) { row ->
                    LocationLine(row, depth = 0, path = null, onPick = { model.showContents(row.id) }, onAdd = null)
                }
                item(key = "items") { Section("Items") }
            }
            when {
                items == null -> if (!model.offline) item(key = "loading") {
                    Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                }
                items.isEmpty() -> item(key = "empty") {
                    Hint(when {
                        screen.locationId == null -> "The Inbox is empty."
                        inside.isNotEmpty() -> "No items in here yet."
                        else -> "Nothing in here yet."
                    })
                }
                else -> items(items, key = { "i-" + it.id }) { row ->
                    // Only the ones in a location further down say where they are.
                    ItemLine(model, row, showPath = row.locationId != screen.locationId) { model.openItem(row) }
                }
            }
        }
    }
}

// One item: rename it, edit its keywords, move it or delete it. Each change is saved right away.
@Composable
fun ItemScreen(model: MainViewModel) {
    val row = model.item
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().imePadding()) {
        Row(Modifier.padding(start = 4.dp, end = 16.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = model::back) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back") }
            Text("Item", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            if (model.itemBusy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
        }
        OfflineBanner(model)
        if (row == null) return@Column
        Column(
            Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
                ItemPhoto(model, row, thumb = false, Modifier.height(280.dp).aspectRatio(3f / 4f).clip(RoundedCornerShape(12.dp)))
            }
            NameField(model, row)
            LocationField(model, row)
            KeywordsField(model, row)
            row.notes?.let {
                Column {
                    Text("Notes", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(it, style = MaterialTheme.typography.bodyLarge)
                }
            }
            if (row.quantity > 1) Text("Quantity: ${row.quantity}", style = MaterialTheme.typography.bodyLarge)
            DeleteButton(model, row)
        }
    }
}

@Composable
private fun NameField(model: MainViewModel, row: PhoneItem) {
    val focusManager = LocalFocusManager.current
    var name by remember(row.id, row.name) { mutableStateOf(row.name ?: "") }
    val changed = name.trim() != (row.name ?: "")
    fun save() {
        if (changed) model.rename(name)
        focusManager.clearFocus()
    }
    OutlinedTextField(
        value = name,
        onValueChange = { name = it },
        label = { Text("Name") },
        placeholder = { Text(if (row.naming) "AI is naming it…" else "Unnamed item") },
        singleLine = true,
        keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Done),
        keyboardActions = KeyboardActions(onDone = { save() }),
        trailingIcon = if (changed) {
            { IconButton(onClick = ::save) { Icon(Icons.Outlined.Check, contentDescription = "Save the name") } }
        } else null,
        modifier = Modifier.fillMaxWidth(),
    )
}

@Composable
private fun LocationField(model: MainViewModel, row: PhoneItem) {
    Surface(
        modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable(onClick = model::startMove),
        color = MaterialTheme.colorScheme.surfaceContainer,
        shape = RoundedCornerShape(12.dp),
    ) {
        Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(if (row.locationId == null) Icons.Outlined.Inbox else Icons.Outlined.Place, contentDescription = null,
                tint = MaterialTheme.colorScheme.primary)
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f)) {
                Text("Location", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(row.path, style = MaterialTheme.typography.bodyLarge)
            }
            TextButton(onClick = model::startMove, enabled = !model.itemBusy) { Text("Move") }
        }
    }
}

// Saved as they change, like on the web. Tapping a chip takes it off.
@Composable
private fun KeywordsField(model: MainViewModel, row: PhoneItem) {
    val colors = remember(model.keywords) { model.keywords.associate { it.keyword to it.color } }
    var typed by rememberSaveable(row.id) { mutableStateOf("") }
    val suggestions = remember(typed, model.keywords, row.keywords) {
        val t = typed.trim().lowercase()
        if (t.isEmpty()) emptyList()
        else model.keywords.map { it.keyword }.filter { it !in row.keywords && it.contains(t) }
            .sortedBy { if (it.startsWith(t)) 0 else 1 }.take(6)
    }
    fun add(word: String) {
        model.addKeyword(word)
        typed = ""
    }

    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text("Keywords", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (row.keywords.isNotEmpty()) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.keywords.forEach { word ->
                    InputChip(
                        selected = false,
                        onClick = { model.removeKeyword(word) },
                        enabled = !model.itemBusy,
                        label = { Text(word) },
                        leadingIcon = colors[word]?.let(::keywordColor)?.let { color ->
                            { Box(Modifier.size(10.dp).clip(CircleShape).background(color)) }
                        },
                        trailingIcon = { Icon(Icons.Outlined.Close, contentDescription = "Remove $word", Modifier.size(18.dp)) },
                    )
                }
            }
        }
        // Above the field, so the keyboard doesn't cover them.
        if (suggestions.isNotEmpty()) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                suggestions.forEach { word -> SuggestionChip(onClick = { add(word) }, label = { Text(word) }) }
            }
        }
        OutlinedTextField(
            value = typed,
            onValueChange = { typed = it },
            placeholder = { Text("Add a keyword") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { add(typed) }),
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

@Composable
private fun DeleteButton(model: MainViewModel, row: PhoneItem) {
    var confirm by remember { mutableStateOf(false) }
    OutlinedButton(
        onClick = { confirm = true },
        enabled = !model.itemBusy,
        colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
    ) {
        Icon(Icons.Outlined.Delete, contentDescription = null, Modifier.size(18.dp))
        Spacer(Modifier.width(8.dp))
        Text("Delete item")
    }
    if (confirm) {
        AlertDialog(
            onDismissRequest = { confirm = false },
            properties = DialogProperties(dismissOnClickOutside = false),
            title = { Text("Delete ${row.shownName}?") },
            text = { Text("It's deleted for everyone in the household, with its photo. This can't be undone.") },
            confirmButton = {
                TextButton(
                    onClick = { confirm = false; model.deleteItem() },
                    colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.error),
                ) { Text("Delete") }
            },
            dismissButton = { TextButton(onClick = { confirm = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun ItemLine(model: MainViewModel, row: PhoneItem, showPath: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        ItemPhoto(model, row, thumb = true, Modifier.size(56.dp).clip(RoundedCornerShape(8.dp)))
        Spacer(Modifier.width(16.dp))
        Column(Modifier.weight(1f)) {
            Text(
                row.shownName,
                style = MaterialTheme.typography.bodyLarge,
                color = if (row.name == null) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
            if (showPath) {
                Text(row.path, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 1, overflow = TextOverflow.StartEllipsis)
            }
        }
        if (row.quantity > 1) Text("×${row.quantity}", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

// The item's photo from the server; the item page shows the thumbnail until the full one is in.
@Composable
private fun ItemPhoto(model: MainViewModel, row: PhoneItem, thumb: Boolean, modifier: Modifier) {
    var bitmap by remember(row.id, row.photoVersion, thumb) {
        mutableStateOf(model.cachedPhoto(row, thumb) ?: if (thumb) null else model.cachedPhoto(row, true))
    }
    LaunchedEffect(row.id, row.photoVersion, thumb) {
        if (model.cachedPhoto(row, thumb) == null) model.loadPhoto(row, thumb)?.let { bitmap = it }
    }
    Box(modifier.background(MaterialTheme.colorScheme.surfaceContainerHigh), contentAlignment = Alignment.Center) {
        val shown = bitmap
        if (shown != null) {
            Image(shown, contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        } else {
            Icon(Icons.Outlined.Image, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun OfflineBanner(model: MainViewModel) {
    if (!model.offline) return
    Surface(
        color = MaterialTheme.colorScheme.errorContainer,
        contentColor = MaterialTheme.colorScheme.onErrorContainer,
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
    ) {
        Row(Modifier.padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.CloudOff, contentDescription = null)
            Spacer(Modifier.width(12.dp))
            Text("Can't reach the server. Find works only online; adding still works offline.",
                style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
            TextButton(onClick = { model.retry() }) { Text("Retry") }
        }
    }
}

@Composable
private fun Section(title: String) {
    Text(
        title,
        style = MaterialTheme.typography.labelLarge,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 4.dp),
    )
}

// The web app's keyword palette (app.css, --c-*), as a dot on the chip.
private fun keywordColor(key: String): Color? = when (key) {
    "red" -> Color(0xFFE5625A)
    "orange" -> Color(0xFFE58A3C)
    "yellow" -> Color(0xFFE8C547)
    "green" -> Color(0xFF5DBB6A)
    "teal" -> Color(0xFF2FB3A3)
    "blue" -> Color(0xFF4B9BE0)
    "purple" -> Color(0xFFA57AD8)
    "pink" -> Color(0xFFEC8FBD)
    "brown" -> Color(0xFF9A7352)
    "gray" -> Color(0xFF8D9896)
    "black" -> Color(0xFF3D3647)
    "white" -> Color(0xFFE9ECEA)
    else -> null
}

// The locations right inside a parent (null: the top ones, and those whose parent isn't known yet), by name.
private fun childrenOf(parentId: String?, all: List<LocationRow>): List<LocationRow> {
    val ids = if (parentId == null) all.mapTo(HashSet()) { it.id } else emptySet()
    return all.filter { if (parentId == null) it.parentId == null || it.parentId !in ids else it.parentId == parentId }
        .sortedBy { it.name.lowercase() }
}
