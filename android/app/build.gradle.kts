plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
    alias(libs.plugins.ksp)
}

// Release builds get their version from the tag (-PtrovamiVersion=1.2.3); local builds are "dev",
// which never offers updates or shows release notes.
val trovamiVersion = (findProperty("trovamiVersion") as String?)?.removePrefix("v") ?: "dev"
val versionParts = Regex("""^(\d+)\.(\d+)\.(\d+)$""").find(trovamiVersion)?.groupValues?.drop(1)?.map(String::toInt)

android {
    namespace = "io.github.geeforceone.trovami"
    compileSdk = 37

    defaultConfig {
        applicationId = "io.github.geeforceone.trovami"
        minSdk = 29
        // Not 37: Android 17 then blocks the local network unless the user grants a permission, and
        // a Trovami server is usually at home. No Play Store requirement pushes this up.
        targetSdk = 36
        versionName = trovamiVersion
        versionCode = versionParts?.let { (major, minor, patch) -> major * 10000 + minor * 100 + patch } ?: 1
    }

    // The release workflow restores the keystore and sets these. Without them a release build stays
    // unsigned, so nothing secret is ever needed to build locally.
    val keystoreFile = System.getenv("TROVAMI_KEYSTORE_FILE")
    signingConfigs {
        if (keystoreFile != null) {
            create("release") {
                storeFile = file(keystoreFile)
                storePassword = System.getenv("TROVAMI_KEYSTORE_PASSWORD")
                keyAlias = System.getenv("TROVAMI_KEY_ALIAS")
                keyPassword = System.getenv("TROVAMI_KEYSTORE_PASSWORD")
            }
        }
    }

    buildTypes {
        release {
            if (keystoreFile != null) signingConfig = signingConfigs.getByName("release")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }
}

ksp {
    arg("room.schemaLocation", "$projectDir/schemas")
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.compose.material.icons.extended)
    debugImplementation(libs.compose.ui.tooling)

    implementation(libs.room.runtime)
    implementation(libs.room.ktx)
    ksp(libs.room.compiler)
    implementation(libs.work.runtime.ktx)
    implementation(libs.datastore.preferences)

    implementation(libs.camerax.camera2)
    implementation(libs.camerax.lifecycle)
    implementation(libs.camerax.view)
    implementation(libs.mlkit.barcode)

    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)

    testImplementation(libs.junit)
}
