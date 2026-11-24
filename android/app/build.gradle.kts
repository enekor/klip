import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

// Release signing: keystore + passwords live OUTSIDE the repo (default ~/.klip-signing/keystore.properties,
// or the path in KLIP_SIGNING). Without it, release builds are left unsigned.
val signingProps = Properties().apply {
    val file = File(System.getenv("KLIP_SIGNING") ?: "${System.getProperty("user.home")}/.klip-signing/keystore.properties")
    if (file.exists()) file.inputStream().use(::load)
}

android {
    namespace = "com.n3k0chan.klip"
    compileSdk {
        version = release(37) { minorApiLevel = 2 }
    }

    defaultConfig {
        applicationId = "com.n3k0chan.klip"
        minSdk = 29
        // 36 on purpose: Android 17 (API 37) adds a local-network permission for apps that target it.
        targetSdk = 36
        versionCode = 3
        versionName = "0.3.0"
    }

    signingConfigs {
        if (signingProps.isNotEmpty()) {
            create("release") {
                storeFile = File(signingProps.getProperty("storeFile"))
                storePassword = signingProps.getProperty("storePassword")
                keyAlias = signingProps.getProperty("keyAlias")
                keyPassword = signingProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            // No minification yet: Ktor and kotlinx.serialization would need keep rules.
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    buildFeatures {
        compose = true
    }

    packaging {
        resources {
            excludes += setOf(
                "META-INF/INDEX.LIST",
                "META-INF/io.netty.versions.properties",
                "META-INF/{AL2.0,LGPL2.1}",
                "META-INF/versions/9/previous-compilation-data.bin",
            )
        }
    }
}

dependencies {
    implementation("com.n3k0chan.klip:klip-node")

    implementation(platform("androidx.compose:compose-bom:2026.11.01"))
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.activity:activity-compose:1.13.0")
    implementation("androidx.core:core-ktx:1.19.1")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.11.1")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.11.1")
}
