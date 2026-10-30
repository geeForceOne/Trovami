package io.github.geeforceone.trovami.ui

import android.nfc.NfcAdapter
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material.icons.outlined.ExpandMore
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.outlined.Nfc
import androidx.compose.material.icons.outlined.Search
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.compose.currentStateAsState
import io.github.geeforceone.trovami.data.LocationRow
import kotlinx.coroutines.launch

// An unknown tag: link it to a location, or to a new one made right here.
@Composable
fun LinkTagScreen(model: MainViewModel, screen: Screen.LinkTag) {
    LocationPicker(
        model = model,
        title = if (screen.written) "Tag set up" else "New tag",
        subtitle = "Where does this tag go? It's linked to the location you pick.",
        onPick = { model.link(screen.code, it, screen.setup) },
        onBack = { model.screen = if (screen.setup) Screen.SetUpTags else Screen.Camera },
    )
}

// Aim the camera without a tag. The camera is closed here, so a tag can be scanned instead.
@Composable
fun PickTargetScreen(model: MainViewModel) {
    LocationPicker(
        model = model,
        title = "Add to…",
        subtitle = "Hold a tag to the phone, or pick the location new photos go to.",
        onPick = { model.aimAt(it) },
        onInbox = { model.aimAt(null) },
    )
}

// Tags one after another: each new or unknown tag is linked to a location, then the next one.
@Composable
fun SetUpTagsScreen(model: MainViewModel) {
    val context = LocalContext.current
    // Checked again on return from the phone's settings.
    val lifecycle by LocalLifecycleOwner.current.lifecycle.currentStateAsState()
    val nfcOn = remember(lifecycle) { NfcAdapter.getDefaultAdapter(context)?.isEnabled == true }
    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding()) {
        Row(Modifier.padding(start = 4.dp, end = 16.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { model.screen = Screen.Camera }) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back") }
            Text("Set up tags", style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            HouseholdSwitcher(model)
        }
        Column(
            Modifier.fillMaxSize().padding(32.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Icon(Icons.Outlined.Nfc, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(72.dp))
            val found = model.setUpFound
            when {
                !nfcOn -> Text("Turn on NFC in the phone's settings first.", style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center)
                found != null -> {
                    val (_, location) = found
                    Text(
                        if (location == null) "This tag is linked to a location that's gone."
                        else "This tag goes to ${location.name}.",
                        style = MaterialTheme.typography.titleMedium,
                        textAlign = TextAlign.Center,
                    )
                    OutlinedButton(onClick = model::relinkFound) { Text("Link it somewhere else") }
                    SetUpHint("Or hold the next tag to the phone.")
                }
                else -> {
                    model.setUpLinked?.let {
                        Text("Linked to $it", style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.primary, textAlign = TextAlign.Center)
                    }
                    Text(
                        if (model.setUpLinked == null) "Hold a new tag to the back of the phone" else "Hold the next tag to the phone",
                        style = MaterialTheme.typography.titleLarge,
                        textAlign = TextAlign.Center,
                    )
                    SetUpHint("A blank tag gets a Trovami code, then you pick where it goes.")
                }
            }
        }
    }
}

@Composable
private fun SetUpHint(text: String) {
    Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center)
}

@Composable
fun ScanQrScreen(model: MainViewModel) {
    Box(Modifier.fillMaxSize().background(Color.Black)) {
        WithCameraPermission {
            QrScanner(Modifier.fillMaxSize()) { raw ->
                model.screen = Screen.Camera
                model.openUri(raw)
            }
        }
        Row(Modifier.fillMaxWidth().statusBarsPadding().padding(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Surface(color = Color.Black.copy(alpha = 0.55f), contentColor = Color.White, shape = RoundedCornerShape(24.dp)) {
                Row(Modifier.padding(end = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = { model.screen = Screen.Camera }) {
                        Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back")
                    }
                    Text("Point the camera at a Trovami label", style = MaterialTheme.typography.titleSmall)
                }
            }
        }
    }
}

// The household's locations as a tree, with a search box and inline "new location" rows.
@Composable
private fun LocationPicker(
    model: MainViewModel,
    title: String,
    subtitle: String,
    onPick: (LocationRow) -> Unit,
    onInbox: (() -> Unit)? = null,
    onBack: () -> Unit = { model.screen = Screen.Camera },
) {
    val locations by model.locations.collectAsStateWithLifecycle()
    var query by rememberSaveable { mutableStateOf("") }
    // The location a new one is being added under; "" for the top level, null when not adding.
    var addingUnder by rememberSaveable { mutableStateOf<String?>(null) }
    val tree = remember(locations) { flattenTree(locations) }
    val hits = remember(locations, query) {
        val q = query.trim()
        if (q.isEmpty()) emptyList()
        else locations.filter { it.name.contains(q, ignoreCase = true) || it.label?.contains(q, ignoreCase = true) == true }
            .sortedBy { pathOf(it, locations).lowercase() }
    }

    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().imePadding()) {
        Row(Modifier.padding(start = 4.dp, end = 16.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back") }
            Text(title, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            HouseholdSwitcher(model)
        }
        Text(
            subtitle,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 16.dp),
        )
        OutlinedTextField(
            value = query,
            onValueChange = { query = it },
            leadingIcon = { Icon(Icons.Outlined.Search, contentDescription = null) },
            placeholder = { Text("Find a location") },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(16.dp),
        )

        LazyColumn(Modifier.fillMaxSize()) {
            if (query.isNotBlank()) {
                if (hits.isEmpty()) item { Hint("No location matches \"${query.trim()}\".") }
                items(hits, key = { it.id }) { row ->
                    LocationLine(row, depth = 0, path = pathOf(row, locations), onPick = { onPick(row) }, onAdd = null)
                }
                return@LazyColumn
            }
            if (onInbox != null) {
                item(key = "inbox") {
                    PickRow(onClick = onInbox) {
                        Icon(Icons.Outlined.Inbox, contentDescription = null, tint = MaterialTheme.colorScheme.secondary)
                        Spacer(Modifier.width(16.dp))
                        Text("Inbox", style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
                    }
                    HorizontalDivider()
                }
            }
            if (tree.isEmpty()) item { Hint("No locations yet. Add the first one below.") }
            items(tree, key = { it.first.id }) { (row, depth) ->
                Column {
                    LocationLine(row, depth, path = null, onPick = { onPick(row) }, onAdd = { addingUnder = row.id })
                    if (addingUnder == row.id) {
                        NewLocation(model, parent = row, depth = depth + 1, onDone = { addingUnder = null }, onPick = onPick)
                    }
                }
            }
            item(key = "new-top") {
                if (addingUnder == "") {
                    NewLocation(model, parent = null, depth = 0, onDone = { addingUnder = null }, onPick = onPick)
                } else {
                    TextButton(onClick = { addingUnder = "" }, modifier = Modifier.padding(8.dp)) {
                        Icon(Icons.Outlined.Add, contentDescription = null)
                        Spacer(Modifier.width(8.dp))
                        Text("New location at the top")
                    }
                }
            }
        }
    }
}

@Composable
private fun LocationLine(row: LocationRow, depth: Int, path: String?, onPick: () -> Unit, onAdd: (() -> Unit)?) {
    PickRow(onClick = onPick, depth = depth) {
        Icon(Kind.of(row.kind).icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.width(16.dp))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(row.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                row.label?.let {
                    Spacer(Modifier.width(8.dp))
                    Text(it, style = MaterialTheme.typography.labelMedium, fontFamily = FontFamily.Monospace, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            if (path != null) {
                Text(path, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.StartEllipsis)
            }
        }
        if (onAdd != null) {
            IconButton(onClick = onAdd) { Icon(Icons.Outlined.Add, contentDescription = "New location in ${row.name}") }
        }
    }
}

@Composable
private fun PickRow(onClick: () -> Unit, depth: Int = 0, content: @Composable RowScope.() -> Unit) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = 56.dp).clickable(onClick = onClick).padding(start = 16.dp + 20.dp * depth.coerceAtMost(8), end = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        content()
    }
}

// Name and kind of a new location, inline under its parent. Saving it picks it right away.
@Composable
private fun NewLocation(model: MainViewModel, parent: LocationRow?, depth: Int, onDone: () -> Unit, onPick: (LocationRow) -> Unit) {
    val scope = rememberCoroutineScope()
    var name by rememberSaveable { mutableStateOf("") }
    var kind by rememberSaveable { mutableStateOf(Kind.Box) }
    var kindsOpen by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }

    fun save() {
        if (name.isBlank()) return
        scope.launch { model.createLocation(parent, name, kind.name)?.let(onPick) }
    }

    Column(
        Modifier.fillMaxWidth().padding(start = 16.dp + 20.dp * depth.coerceAtMost(8), end = 16.dp, top = 4.dp, bottom = 12.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        OutlinedTextField(
            value = name,
            onValueChange = { name = it },
            label = { Text(if (parent == null) "New location" else "New location in ${parent.name}") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { save() }),
            modifier = Modifier.fillMaxWidth().focusRequester(focus),
        )
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
            Box {
                OutlinedButton(onClick = { kindsOpen = true }) {
                    Icon(kind.icon, contentDescription = null, Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Text(kind.label)
                    Icon(Icons.Outlined.ExpandMore, contentDescription = null, Modifier.size(18.dp))
                }
                DropdownMenu(expanded = kindsOpen, onDismissRequest = { kindsOpen = false }) {
                    Kind.entries.forEach { k ->
                        DropdownMenuItem(
                            text = { Text(k.label) },
                            leadingIcon = { Icon(k.icon, contentDescription = null) },
                            onClick = { kind = k; kindsOpen = false },
                        )
                    }
                }
            }
            Spacer(Modifier.weight(1f))
            TextButton(onClick = onDone) { Text("Cancel") }
            Button(onClick = ::save, enabled = name.isNotBlank()) { Text("Save") }
        }
    }
}

// Only shown when the user is in more than one household.
@Composable
fun HouseholdSwitcher(model: MainViewModel) {
    val households by model.households.collectAsStateWithLifecycle()
    val prefs by model.prefState.collectAsStateWithLifecycle()
    if (households.size < 2) return
    var open by remember { mutableStateOf(false) }
    Box {
        TextButton(onClick = { open = true }) {
            Text(households.firstOrNull { it.id == prefs?.householdId }?.name ?: "Household", maxLines = 1, overflow = TextOverflow.Ellipsis)
            Icon(Icons.Outlined.ExpandMore, contentDescription = null)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            households.forEach { h ->
                DropdownMenuItem(text = { Text(h.name) }, onClick = { open = false; model.switchHousehold(h.id) })
            }
        }
    }
}

@Composable
private fun Hint(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp),
    )
}
