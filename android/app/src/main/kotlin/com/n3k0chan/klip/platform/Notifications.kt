package com.n3k0chan.klip.platform

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import com.n3k0chan.klip.R
import com.n3k0chan.klip.ui.MainActivity

object Notifications {
    private const val CHANNEL_SERVICE = "receiver"
    private const val CHANNEL_EVENTS = "events"
    const val ID_SERVICE = 1
    private const val ID_TEST = 2
    private const val ID_TOO_LARGE = 3

    fun createChannels(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_SERVICE, "Receptor en segundo plano", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Aviso permanente mientras Klip puede recibir el portapapeles"
                setShowBadge(false)
            }
        )
        nm.createNotificationChannel(
            NotificationChannel(CHANNEL_EVENTS, "Avisos", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Pruebas de conexión recibidas y errores"
            }
        )
    }

    fun service(context: Context): Notification =
        Notification.Builder(context, CHANNEL_SERVICE)
            .setSmallIcon(R.drawable.ic_tile)
            .setContentTitle("Klip activo")
            .setContentText("Recibiendo el portapapeles de tus dispositivos")
            .setContentIntent(openApp(context))
            .setOngoing(true)
            .apply { if (Build.VERSION.SDK_INT >= 31) setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE) }
            .build()

    /** Shows the received test text itself, so the user sees exactly what arrived. */
    fun testReceived(context: Context, from: String, content: String) =
        notify(context, ID_TEST, "Prueba recibida de $from", content)

    fun tooLarge(context: Context, chars: Int) =
        notify(context, ID_TOO_LARGE, "Texto demasiado grande", "Llegó un texto de $chars caracteres que no cabe en el portapapeles de Android")

    private fun notify(context: Context, id: Int, title: String, text: String) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return
        context.getSystemService(NotificationManager::class.java).notify(
            id,
            Notification.Builder(context, CHANNEL_EVENTS)
                .setSmallIcon(R.drawable.ic_tile)
                .setContentTitle(title)
                .setContentText(text)
                .setStyle(Notification.BigTextStyle().bigText(text))
                .setContentIntent(openApp(context))
                .setAutoCancel(true)
                .build(),
        )
    }

    private fun openApp(context: Context): PendingIntent =
        PendingIntent.getActivity(
            context, 0,
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
}
