package io.github.geeforceone.trovami.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import kotlinx.coroutines.launch

@Composable
fun TrovamiRoot(model: MainViewModel) {
    val prefs by model.prefState.collectAsStateWithLifecycle()
    val snackbar = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    LaunchedEffect(model) {
        // A new message replaces the one on screen instead of queueing behind it.
        model.messages.collect { message ->
            snackbar.currentSnackbarData?.dismiss()
            scope.launch { snackbar.showSnackbar(message) }
        }
    }

    val state = prefs
    val signedIn = state?.session != null
    BackHandler(enabled = signedIn && model.screen != Screen.Camera) { model.screen = Screen.Camera }

    Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)) {
        when {
            state == null -> Unit
            !signedIn -> SignInScreen(model)
            else -> when (val screen = model.screen) {
                Screen.Camera -> CameraScreen(model)
                is Screen.LinkTag -> LinkTagScreen(model, screen)
                Screen.PickTarget -> PickTargetScreen(model)
                Screen.ScanQr -> ScanQrScreen(model)
                Screen.Settings -> SettingsScreen(model)
            }
        }
        // Above the camera's shutter row, so a message never hides it.
        val lift = if (signedIn && model.screen == Screen.Camera) 132.dp else 8.dp
        SnackbarHost(
            snackbar,
            Modifier.align(Alignment.BottomCenter).navigationBarsPadding().imePadding().padding(bottom = lift),
        )
    }

    model.updatedTo?.let { release ->
        val uriHandler = LocalUriHandler.current
        AlertDialog(
            onDismissRequest = model::dismissUpdatedTo,
            properties = DialogProperties(dismissOnClickOutside = false),
            title = { Text("Trovami was updated to ${release.version}") },
            text = {
                Text(
                    release.notes?.let(::plainNotes)?.ifBlank { null } ?: "See the release notes for what's new.",
                    Modifier.heightIn(max = 360.dp).verticalScroll(rememberScrollState()),
                )
            },
            confirmButton = { TextButton(onClick = model::dismissUpdatedTo) { Text("Close") } },
            dismissButton = if (release.pageUrl.isNotBlank()) {
                { TextButton(onClick = { uriHandler.openUri(release.pageUrl) }) { Text("Release notes") } }
            } else null,
        )
    }
}

// Release notes are Markdown (CHANGELOG.md); shown as plain text with the markup taken out.
private fun plainNotes(markdown: String): String = markdown.lines().joinToString("\n") { line ->
    line.replace(Regex("""^#{1,6}\s*"""), "")
        .replace(Regex("""^\s*[-*]\s+"""), "• ")
        .replace(Regex("""\*\*(.+?)\*\*"""), "$1")
        .replace(Regex("""\[(.+?)]\(.+?\)"""), "$1")
        .replace("`", "")
}.trim()
