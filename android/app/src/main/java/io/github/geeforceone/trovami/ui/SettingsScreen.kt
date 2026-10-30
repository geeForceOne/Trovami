package io.github.geeforceone.trovami.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.ArrowBack
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import io.github.geeforceone.trovami.BuildConfig
import java.text.DateFormat
import java.util.Date

@Composable
fun SettingsScreen(model: MainViewModel) {
    val prefs by model.prefState.collectAsStateWithLifecycle()
    val households by model.households.collectAsStateWithLifecycle()
    val waiting by model.waiting.collectAsStateWithLifecycle()
    val state = prefs ?: return
    val session = state.session ?: return
    var confirmSignOut by rememberSaveable { mutableStateOf(false) }

    Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().verticalScroll(rememberScrollState())) {
        Row(Modifier.padding(start = 4.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { model.screen = Screen.Camera }) { Icon(Icons.AutoMirrored.Outlined.ArrowBack, contentDescription = "Back") }
            Text("Settings", style = MaterialTheme.typography.titleLarge)
        }

        Section("Account") {
            Text(session.name, style = MaterialTheme.typography.bodyLarge)
            session.email?.let { Muted(it) }
            Muted(session.server)
        }

        Section("Household") {
            when {
                households.isEmpty() -> Muted("You're not in a household yet. Create one in the web app, then sync.")
                households.size == 1 -> Text(households[0].name, style = MaterialTheme.typography.bodyLarge)
                else -> households.forEach { h ->
                    Row(
                        Modifier.fillMaxWidth().clickable { model.switchHousehold(h.id) },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(selected = h.id == state.householdId, onClick = { model.switchHousehold(h.id) })
                        Text(h.name, style = MaterialTheme.typography.bodyLarge)
                    }
                }
            }
        }

        Section("Sync") {
            Text(
                if (waiting == 0) "Everything is uploaded." else if (waiting == 1) "1 item is waiting to upload." else "$waiting items are waiting to upload.",
                style = MaterialTheme.typography.bodyLarge,
            )
            Muted(
                if (state.lastSync == 0L) "Not synced yet."
                else "Last synced " + DateFormat.getDateTimeInstance(DateFormat.MEDIUM, DateFormat.SHORT).format(Date(state.lastSync))
            )
            state.problem?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) }
            OutlinedButton(onClick = model::syncNow) { Text("Sync now") }
        }

        Section("App") {
            Text("Trovami ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodyLarge)
            OutlinedButton(onClick = { model.checkForUpdate(force = true) }) { Text("Check for updates") }
        }

        Section(null) {
            OutlinedButton(
                onClick = { confirmSignOut = true },
                colors = ButtonDefaults.outlinedButtonColors(contentColor = MaterialTheme.colorScheme.error),
            ) { Text("Sign out") }
        }
    }

    if (confirmSignOut) {
        AlertDialog(
            onDismissRequest = { confirmSignOut = false },
            properties = DialogProperties(dismissOnClickOutside = false),
            title = { Text("Sign out of this phone?") },
            text = {
                Text(
                    if (waiting > 0) "$waiting ${if (waiting == 1) "item hasn't" else "items haven't"} been uploaded yet and will be lost."
                    else "You can sign in again any time."
                )
            },
            confirmButton = {
                TextButton(onClick = { confirmSignOut = false; model.signOut() }) {
                    Text("Sign out", color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = { TextButton(onClick = { confirmSignOut = false }) { Text("Cancel") } },
        )
    }
}

@Composable
private fun Section(title: String?, content: @Composable () -> Unit) {
    HorizontalDivider(Modifier.padding(top = 8.dp))
    Column(Modifier.fillMaxWidth().padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        title?.let { Text(it, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary) }
        content()
    }
}

@Composable
private fun Muted(text: String) {
    Text(text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
}
