import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
}

// Release signing: keystore + passwords live OUTSIDE the repo. First existing file wins:
//   $KLIP_SIGNING, ~/credenciales/Android/keystore.properties (personal credentials repo).
// Without one, release builds are left unsigned.
val signingFile: File? = listOfNotNull(
    System.getenv("KLIP_SIGNING"),
    "${System.getProperty("user.home")}/credenciales/Android/keystore.properties",
).map(::File).firstOrNull { it.exists() }

val signingProps = Properties().apply { signingFile?.inputStream()?.use(::load) }

/**
 * storeFile may be a path from another machine (e.g. a Windows path): if it does not exist here, look for a file
 * with the same name next to the properties file or in its parent folder.
 */
fun resolveStoreFile(path: String): File {
    val direct = File(path)
    if (direct.exists()) return direct
    val name = path.substringAfterLast('\\').substringAfterLast('/')
    return listOfNotNull(signingFile?.parentFile, signingFile?.parentFile?.parentFile)
        .map { File(it, name) }
        .firstOrNull { it.exists() }
        ?: error("Keystore '$path' not found (also looked for '$name' next to $signingFile)")
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
        versionCode = 4
        versionName = "0.4.0"
    }

    signingConfigs {
        if (signingProps.isNotEmpty()) {
            create("release") {
                storeFile = resolveStoreFile(signingProps.getProperty("storeFile"))
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
