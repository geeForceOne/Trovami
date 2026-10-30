package io.github.geeforceone.trovami

import android.app.Application
import io.github.geeforceone.trovami.data.AppDb
import io.github.geeforceone.trovami.data.Prefs
import io.github.geeforceone.trovami.net.Api

// The app's few singletons, made on first use.
class TrovamiApp : Application() {
    val prefs by lazy { Prefs(this) }
    val db by lazy { AppDb.open(this) }
    val api by lazy { Api(BuildConfig.VERSION_NAME) }
}
