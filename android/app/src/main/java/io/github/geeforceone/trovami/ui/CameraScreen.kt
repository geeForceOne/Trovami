package io.github.geeforceone.trovami.ui

import android.nfc.NfcAdapter
import androidx.camera.core.ImageCapture
import androidx.camera.core.ImageCaptureException
import androidx.camera.core.ImageProxy
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Add
import androidx.compose.material.icons.outlined.Close
import androidx.compose.material.icons.outlined.CloudDone
import androidx.compose.material.icons.outlined.CloudUpload
import androidx.compose.material.icons.outlined.EditNote
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.outlined.Menu
import androidx.compose.material.icons.outlined.Nfc
import androidx.compose.material.icons.outlined.Place
import androidx.compose.material.icons.outlined.QrCodeScanner
import androidx.compose.material.icons.outlined.Remove
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.delay
import java.util.concurrent.Executors

private val Scrim = Color.Black.copy(alpha = 0.55f)

// The app's home: the camera, aimed at the Inbox or at the location of the last scanned tag.
// Each press of the shutter saves an item; the camera stays ready for the next one, until it's idle.
@Composable
fun CameraScreen(model: MainViewModel) {
    val capture = remember { photoCapture() }
    val worker = remember { Executors.newSingleThreadExecutor() }
    DisposableEffect(worker) { onDispose { worker.shutdown() } }
    var flash by remember { mutableStateOf(false) }
    var detailsOpen by rememberSaveable { mutableStateOf(false) }
    // Bumped by each photo, so the idle time starts again.
    var shots by remember { mutableIntStateOf(0) }

    LaunchedEffect(flash) {
        if (flash) {
            delay(120)
            flash = false
        }
    }

    // Some phones read no tags while the camera is open, so it closes when it isn't used.
    LaunchedEffect(model.cameraPaused, detailsOpen, shots) {
        if (!model.cameraPaused && !detailsOpen) {
            delay(CAMERA_IDLE_MILLIS)
            model.cameraPaused = true
        }
    }

    fun shoot() {
        shots++
        val typed = model.details
        model.details = Details()
        detailsOpen = false
        flash = true
        capture.takePicture(worker, object : ImageCapture.OnImageCapturedCallback() {
            override fun onCaptureSuccess(image: ImageProxy) {
                val bitmap = image.toBitmap()
                val rotation = image.imageInfo.rotationDegrees
                image.close()
                model.capture(bitmap, rotation, typed)
            }

            override fun onError(exception: ImageCaptureException) {
                model.say("The photo failed. Try again.")
            }
        })
    }

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (model.cameraPaused) {
            CameraPausedView(onResume = { model.cameraPaused = false })
        } else {
            WithCameraPermission { CameraPreview(Modifier.fillMaxSize(), capture) }
        }
        if (flash) Box(Modifier.fillMaxSize().background(Color.White.copy(alpha = 0.5f)))

        Column(
            Modifier.fillMaxWidth().statusBarsPadding().padding(12.dp),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(Modifier.fillMaxWidth()) {
                TargetChip(model, Modifier.align(Alignment.CenterStart).padding(end = 64.dp))
                Box(Modifier.align(Alignment.CenterEnd)) { CameraMenu(model) }
            }
            UpdateBanner(model)
        }

        Column(
            Modifier.align(Alignment.BottomCenter).fillMaxWidth().navigationBarsPadding().imePadding().padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            if (detailsOpen) DetailsCard(model)
            Box(Modifier.fillMaxWidth()) {
                DetailsButton(model, detailsOpen, { detailsOpen = !detailsOpen }, Modifier.align(Alignment.CenterStart))
                if (!model.cameraPaused) Shutter(::shoot, Modifier.align(Alignment.Center))
                WaitingBadge(model, Modifier.align(Alignment.CenterEnd))
            }
        }
    }
}

private const val CAMERA_IDLE_MILLIS = 20_000L

// The idle camera: tags can be read now. Tapping anywhere opens the camera again.
@Composable
private fun CameraPausedView(onResume: () -> Unit) {
    Column(
        Modifier.fillMaxSize().clickable(onClick = onResume).padding(32.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(Icons.Outlined.Nfc, contentDescription = null, tint = Color.White, modifier = Modifier.size(72.dp))
        Text("Hold a tag to the phone", color = Color.White, style = MaterialTheme.typography.titleLarge, textAlign = TextAlign.Center)
        Text(
            "or tap to take photos",
            color = Color.White.copy(alpha = 0.75f),
            style = MaterialTheme.typography.bodyLarge,
            textAlign = TextAlign.Center,
        )
    }
}

@Composable
private fun TargetChip(model: MainViewModel, modifier: Modifier) {
    val target by model.target.collectAsStateWithLifecycle()
    val locations by model.locations.collectAsStateWithLifecycle()
    val households by model.households.collectAsStateWithLifecycle()
    val prefs by model.prefState.collectAsStateWithLifecycle()
    val household = households.firstOrNull { it.id == prefs?.householdId }

    Surface(
        modifier = modifier.clip(RoundedCornerShape(24.dp)).clickable { model.screen = Screen.PickTarget },
        color = Scrim,
        contentColor = Color.White,
        shape = RoundedCornerShape(24.dp),
    ) {
        Row(Modifier.padding(start = 14.dp, end = 4.dp, top = 6.dp, bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            val current = target
            Icon(current?.let { Kind.of(it.kind).icon } ?: Icons.Outlined.Inbox, contentDescription = null, Modifier.size(20.dp))
            Spacer(Modifier.width(10.dp))
            // The location's own name in full; where it is (or the household) underneath.
            val parent = current?.parentId?.let { id -> locations.firstOrNull { it.id == id } }
            Column(Modifier.weight(1f, fill = false).padding(vertical = 2.dp)) {
                Text(
                    current?.name ?: "Inbox",
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    parent?.let { pathOf(it, locations) } ?: household?.name ?: "No household yet",
                    style = MaterialTheme.typography.bodySmall,
                    color = Color.White.copy(alpha = 0.75f),
                    maxLines = 1,
                    overflow = TextOverflow.StartEllipsis,
                )
            }
            if (current != null) {
                IconButton(onClick = { model.aimAt(null) }) { Icon(Icons.Outlined.Close, contentDescription = "Back to the Inbox") }
            } else {
                Spacer(Modifier.width(10.dp))
            }
        }
    }
}

@Composable
private fun CameraMenu(model: MainViewModel) {
    val context = LocalContext.current
    var open by remember { mutableStateOf(false) }
    Box {
        Surface(color = Scrim, contentColor = Color.White, shape = CircleShape) {
            IconButton(onClick = { open = true }) { Icon(Icons.Outlined.Menu, contentDescription = "Menu") }
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            DropdownMenuItem(
                text = { Text("Scan a QR label") },
                leadingIcon = { Icon(Icons.Outlined.QrCodeScanner, null) },
                onClick = { open = false; model.screen = Screen.ScanQr },
            )
            DropdownMenuItem(
                text = { Text("Add to a location") },
                leadingIcon = { Icon(Icons.Outlined.Place, null) },
                onClick = { open = false; model.screen = Screen.PickTarget },
            )
            DropdownMenuItem(
                text = { Text("Set up tags") },
                leadingIcon = { Icon(Icons.Outlined.Nfc, null) },
                onClick = {
                    open = false
                    if (NfcAdapter.getDefaultAdapter(context) == null) {
                        model.say("This phone has no NFC. Print QR labels in the web app instead.")
                    } else {
                        model.openSetUpTags()
                    }
                },
            )
            DropdownMenuItem(
                text = { Text("Settings") },
                leadingIcon = { Icon(Icons.Outlined.Settings, null) },
                onClick = { open = false; model.screen = Screen.Settings },
            )
        }
    }
}

@Composable
private fun UpdateBanner(model: MainViewModel) {
    val release = model.update ?: return
    Surface(color = MaterialTheme.colorScheme.surfaceContainerHigh, shape = RoundedCornerShape(16.dp)) {
        Column(Modifier.padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Trovami ${release.version} is available", Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                Button(onClick = model::installUpdate, enabled = model.downloading == null) { Text("Update") }
            }
            model.downloading?.let { LinearProgressIndicator(progress = { it }, Modifier.fillMaxWidth().padding(top = 8.dp, end = 8.dp)) }
        }
    }
}

// Name, notes and quantity for the next photo; all optional.
@Composable
private fun DetailsCard(model: MainViewModel) {
    val details = model.details
    Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = RoundedCornerShape(16.dp)) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(
                value = details.name,
                onValueChange = { model.details = details.copy(name = it) },
                label = { Text("Name") },
                placeholder = { Text("Leave empty to let AI name it") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
                modifier = Modifier.fillMaxWidth(),
            )
            OutlinedTextField(
                value = details.notes,
                onValueChange = { model.details = details.copy(notes = it) },
                label = { Text("Notes") },
                maxLines = 3,
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
                modifier = Modifier.fillMaxWidth(),
            )
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("Quantity", Modifier.weight(1f), style = MaterialTheme.typography.bodyLarge)
                IconButton(
                    onClick = { model.details = details.copy(quantity = details.quantity - 1) },
                    enabled = details.quantity > 1,
                ) { Icon(Icons.Outlined.Remove, contentDescription = "Fewer") }
                Text("${details.quantity}", style = MaterialTheme.typography.titleMedium)
                IconButton(onClick = { model.details = details.copy(quantity = details.quantity + 1) }) {
                    Icon(Icons.Outlined.Add, contentDescription = "More")
                }
            }
        }
    }
}

@Composable
private fun DetailsButton(model: MainViewModel, open: Boolean, onToggle: () -> Unit, modifier: Modifier) {
    val name = model.details.name.trim()
    Surface(
        modifier = modifier.clip(RoundedCornerShape(20.dp)).clickable(onClick = onToggle),
        color = if (open) MaterialTheme.colorScheme.primary else Scrim,
        contentColor = if (open) MaterialTheme.colorScheme.onPrimary else Color.White,
        shape = RoundedCornerShape(20.dp),
    ) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.EditNote, contentDescription = null, Modifier.size(20.dp))
            Spacer(Modifier.width(6.dp))
            Text(
                name.ifEmpty { "Details" },
                style = MaterialTheme.typography.labelLarge,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.width(72.dp),
            )
        }
    }
}

@Composable
private fun Shutter(onClick: () -> Unit, modifier: Modifier) {
    Box(
        modifier
            .size(76.dp)
            .border(4.dp, Color.White, CircleShape)
            .padding(7.dp)
            .clip(CircleShape)
            .background(Color.White)
            .clickable(onClick = onClick)
            .semantics { contentDescription = "Take photo" }
    )
}

@Composable
private fun WaitingBadge(model: MainViewModel, modifier: Modifier) {
    val waiting by model.waiting.collectAsStateWithLifecycle()
    Surface(
        modifier = modifier.clip(RoundedCornerShape(20.dp)).clickable { model.screen = Screen.Settings },
        color = Scrim,
        contentColor = Color.White,
        shape = RoundedCornerShape(20.dp),
    ) {
        Row(Modifier.padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            if (waiting > 0) {
                Icon(Icons.Outlined.CloudUpload, contentDescription = "Waiting to upload", Modifier.size(20.dp))
                Spacer(Modifier.width(6.dp))
                Text("$waiting", style = MaterialTheme.typography.labelLarge)
            } else {
                Icon(Icons.Outlined.CloudDone, contentDescription = "Everything is uploaded", Modifier.size(20.dp))
            }
        }
    }
}
