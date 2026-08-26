-- 2026-08-26: la huella abre la pantalla del celu todos los dias; cada 30 dias se vuelve a pedir
-- usuario y clave, pero ADENTRO de la misma pantalla. Esta columna guarda cuando fue la ultima vez
-- que se confirmo la clave en ese telefono. Idempotente: se puede correr las veces que haga falta.
IF EXISTS (SELECT 1 FROM sys.tables WHERE name='WaMovil_WebAuthnCredentials')
   AND NOT EXISTS (SELECT 1 FROM sys.columns
                   WHERE Name='PasswordCheckedAt' AND Object_ID=Object_ID('WaMovil_WebAuthnCredentials'))
    ALTER TABLE WaMovil_WebAuthnCredentials ADD PasswordCheckedAt DATETIME2 NULL;
GO
