rootProject.name = "klip-core"

dependencyResolutionManagement {
    repositories {
        mavenCentral()
    }
}

// Platform-independent node (HTTP server/client, pairing, group, radar) shared by Android and tests.
include(":klip-node")
project(":klip-node").projectDir = file("node")
