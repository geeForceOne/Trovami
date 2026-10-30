package io.github.geeforceone.trovami.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// The web app's colors (wwwroot/css/app.css): deep purple shelves in the dark, green-gray in the light, one teal for actions,
// the logo's amber for "found". The app follows the phone's light or dark setting.
private val Dark = darkColorScheme(
    primary = Color(0xFF5CC9B0),
    onPrimary = Color(0xFF062019),
    primaryContainer = Color(0xFF1C3D36),
    onPrimaryContainer = Color(0xFF7FD8C3),
    secondary = Color(0xFFF2B44C),
    onSecondary = Color(0xFF241500),
    background = Color(0xFF140F1F),
    onBackground = Color(0xFFECE8F2),
    surface = Color(0xFF140F1F),
    onSurface = Color(0xFFECE8F2),
    surfaceVariant = Color(0xFF1D1630),
    onSurfaceVariant = Color(0xFFC9C1D6),
    surfaceContainerLowest = Color(0xFF0F0B18),
    surfaceContainerLow = Color(0xFF1A1428),
    surfaceContainer = Color(0xFF1A1428),
    surfaceContainerHigh = Color(0xFF1D1630),
    surfaceContainerHighest = Color(0xFF261D3B),
    outline = Color(0xFF3A2F52),
    outlineVariant = Color(0xFF2A2240),
    error = Color(0xFFF08A7E),
)

private val Light = lightColorScheme(
    primary = Color(0xFF1F6F62),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFD6EDE7),
    onPrimaryContainer = Color(0xFF175A50),
    secondary = Color(0xFFD98A12),
    onSecondary = Color(0xFFFFFFFF),
    background = Color(0xFFF2F5F4),
    onBackground = Color(0xFF142120),
    surface = Color(0xFFF2F5F4),
    onSurface = Color(0xFF142120),
    surfaceVariant = Color(0xFFEEF3F2),
    onSurfaceVariant = Color(0xFF304240),
    surfaceContainerLowest = Color(0xFFFFFFFF),
    surfaceContainerLow = Color(0xFFFFFFFF),
    surfaceContainer = Color(0xFFFFFFFF),
    surfaceContainerHigh = Color(0xFFEEF3F2),
    surfaceContainerHighest = Color(0xFFE3EAE8),
    outline = Color(0xFFC0CCC9),
    outlineVariant = Color(0xFFDBE3E1),
    error = Color(0xFFB3261E),
)

@Composable
fun TrovamiTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) Dark else Light, content = content)
}
