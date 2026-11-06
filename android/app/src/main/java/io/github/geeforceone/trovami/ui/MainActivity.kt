package io.github.geeforceone.trovami.ui

import android.content.Intent
import android.hardware.camera2.CameraManager
import android.nfc.NfcAdapter
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.lifecycle.Lifecycle

// The only activity. Opened from the launcher, by an NFC tag (even when the app is closed), or by
// a trovami:// link the phone's camera app found in a QR code.
class MainActivity : ComponentActivity() {
    private val model: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { TrovamiTheme { TrovamiRoot(model) } }
        if (savedInstanceState == null) handle(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    // While the app is open it reads every tag itself, blank ones included, so they can be set up.
    override fun onResume() {
        super.onResume()
        enableReaderMode()
        getSystemService(CameraManager::class.java).registerAvailabilityCallback(cameraWatch, handler)
    }

    override fun onPause() {
        super.onPause()
        handler.removeCallbacks(rearm)
        getSystemService(CameraManager::class.java).unregisterAvailabilityCallback(cameraWatch)
        NfcAdapter.getDefaultAdapter(this)?.disableReaderMode(this)
    }

    private fun enableReaderMode() {
        NfcAdapter.getDefaultAdapter(this)?.enableReaderMode(
            this,
            model::onNfcTag,
            NfcAdapter.FLAG_READER_NFC_A or NfcAdapter.FLAG_READER_NFC_B or NfcAdapter.FLAG_READER_NFC_F or NfcAdapter.FLAG_READER_NFC_V,
            null,
        )
    }

    // Some phones (Samsung) switch NFC polling off while a camera is open, and switching it back on
    // also drops this app's reader mode. So it's turned on again once the camera is free, a little
    // later and once more, to land after the system's own call.
    private val handler = Handler(Looper.getMainLooper())
    private val rearm = Runnable { if (lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED)) enableReaderMode() }
    private val cameraWatch = object : CameraManager.AvailabilityCallback() {
        override fun onCameraAvailable(cameraId: String) {
            handler.removeCallbacks(rearm)
            handler.postDelayed(rearm, 400)
            handler.postDelayed(rearm, 1500)
        }
    }

    private fun handle(intent: Intent?) {
        val uri = intent?.data?.toString() ?: return
        model.openUri(uri, external = true)
    }
}
