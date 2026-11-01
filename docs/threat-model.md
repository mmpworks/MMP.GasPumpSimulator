# Threat model

Two trust boundaries, two jobs. The reader protects the card. The controller protects the fuel. This simulator builds the second one and names the first.

## Assets

1. Fuel. Volume leaving the hose that nobody paid for.
2. The hold. Money on a driver's card that should be released or settled to the real amount.
3. The record. A ledger and a totalizer that let the books be checked against the meter.

## Attackers

- **Upstream owned.** The EPS, the site controller, or the wire between them is compromised. The attacker can send any message to the pump, including a correctly signed one.
- **On the wire.** The attacker can capture and replay messages between the payment host and the pump.
- **At the pump.** The attacker can feed the meter input, as a spoofed pulser would.
- **At the host.** The network to the processor is down, slow, or flapping.

## Attacks and controls

| # | Attack | Control | Where | Test |
|---|--------|---------|-------|------|
| 1 | Send the pump a ceiling of $2000 | There is no ceiling message. The only way to arm is a token, and the token's ceiling is verified then clamped. | `PumpMessage` has no SetCeiling; `PumpAuthority.TryArm` | `Arming_without_a_token_is_refused` |
| 2 | Edit the ceiling in a captured token | HMAC-SHA256 over every field. One changed digit, wrong signature, refused before anything else is checked. | `HmacAuthorizationVerifier` | `Tampered_ceiling_fails_the_signature`, `Tampered_token_is_refused_and_the_sale_fails_closed` |
| 3 | Replay a good token | Single-use nonce. The second presentation is refused. A forged token never consumes a nonce because the signature check comes first. | `NonceStore` | `Replayed_token_is_refused_the_second_time`, `Replayed_token_is_refused`, `Expired_token_is_refused_and_a_forged_one_never_burns_a_nonce` |
| 4 | Use a token meant for another pump | Pump id is in the signed payload and checked against the pump's own. | `HmacAuthorizationVerifier` | `Token_for_another_pump_is_refused` |
| 5 | Use a token hours later | Issued and expiry times are in the signed payload. A small skew is allowed. | `HmacAuthorizationVerifier` | `Expired_token_is_refused...` |
| 6 | **Own the upstream and sign a 600-gallon, $2000 token** | The pump owns hard limits set at construction and never from a message. Verified ceiling and volume are clamped to them. The clamp is written to the ledger. | `HardLimits`, `PumpAuthority.TryArm` | `Authority_clamps_a_signed_600_gallon_2000_dollar_token_to_the_hard_limits`, `Signed_600_gallon_token_is_clamped_to_50_gallons_and_500_dollars` |
| 7 | Keep the sale open for hours | Max sale duration, enforced per tick by the pump against its own clock. | `PumpController.Tick` | `Sale_stops_at_the_hard_duration_limit` |
| 8 | Spoof the pulser so the meter reads fuel that did not move | Per-tick pulse sanity against what the valve could pass. Emergency stop on the first bad tick. Totalizer unchanged. | `PumpController.Tick` | `Spoofed_pulses_during_dispensing_stop_the_pump_dead`, `Spoofed_pulses_during_a_sale_fault_the_pump_and_close_the_valve` |
| 9 | Fuel moving while the pump is disarmed (stuck valve, bypass) | Any pulse while not dispensing is a fault. | `PumpController.Tick` | `Fuel_moving_while_disarmed_is_a_fault` |
| 10 | Lift the nozzle with no authorization | Nothing opens. The lift is recorded. | `PumpController.LiftNozzle` | `Nozzle_lift_without_authorization_opens_nothing` |
| 11 | Erase or edit the sale record | Hash-chained ledger. Editing entry N breaks every hash after it. The totalizer is recorded on every valve close. | `EventLedger.Verify`, `Totalizer` | `Ledger_verifies_intact_and_detects_an_edited_entry`, `Totalizer_only_goes_up` |
| 12 | Complete a sale a second time to double-charge | Completion is idempotent per sale id at the issuer. | `IssuerSimulator.Complete` | `Hold_comes_off_the_balance_and_completion_refunds_the_unspent_part` |
| 13 | Leave a hold on a card when no fuel moved | Zero-volume sales reverse the hold instead of completing. No lift in 120 s reverses. | `SaleStateMachine.Finish` | `Hang_with_zero_fuel_reverses_instead_of_completing`, `No_nozzle_lift_for_two_minutes_cancels_and_reverses_the_hold` |
| 14 | Host down at completion | Completion queues and drains when the host returns. The hold stays on until then. | `PaymentGateway` | `Completion_queues_while_the_host_is_down_and_drains_when_it_returns`, `Host_down_at_completion_queues_and_drains_when_it_returns` |
| 15 | Host silent at authorization | 30 s timeout, sale declined, nothing armed. | `PaymentGateway`, `SaleStateMachine` | `Gateway_times_out_after_thirty_seconds_of_silence`, `Network_timeout_at_authorization_declines_after_thirty_seconds` |
| 16 | Exceed the ceiling by a cent through rounding | Price rounds half up, the same way in the limit check and the display. The last slice is clamped to the largest volume that prices at or under the ceiling. | `Pricing.MaxVolumeFor`, `PumpController.Tick` | `MaxVolumeFor_never_prices_over_the_money_and_one_more_pulse_does`, `Thirty_dollar_sale_lands_on_3000_cents_never_over_with_a_visible_taper` |
| 17 | Bank answers after the pump gave up (late approval arms a sale nobody is standing at) | A late `Approved` in any state but `Authorizing` is never armed. The machine reverses it. A late approval for a sale the driver already replaced is reversed by the simulation directly. | `SaleStateMachine.ApplyHoldEvent`, `Simulation.HandleOrphanEvent` | `Late_approval_after_the_timeout_is_reversed_and_never_armed`, `Late_approval_is_reversed_not_armed_and_the_card_ends_untouched`, `Late_approval_for_a_sale_the_driver_already_replaced_is_still_reversed` |
| 18 | Reversal lost on a dead link leaves a hold on a card for a sale that dispensed nothing | Reversals queue in the same store-and-forward list as completions, once per sale, and drain when any call succeeds. The driver is told the release is pending. | `PaymentGateway.ReverseAsync`, `DrainPendingAsync` | `Reversal_that_cannot_reach_the_host_is_queued_once_and_drains_before_completions`, `Bank_unreachable_after_timeout_queues_the_reversal_and_releases_when_the_link_returns` |
| 19 | Reversal arrives at the bank before its own authorization, so the late authorization opens an orphan hold | The issuer records a void for the sale id and declines the late authorization. | `IssuerSimulator.Reverse`, `Authorize` | `Reversal_that_arrives_before_its_authorization_voids_the_sale_at_the_issuer` |
| 20 | Fuel flows with the nozzle out of the tank, or with no grade, or with a lifted nozzle alone | Three interlocks in the pump controller: grade selected, nozzle lifted, nozzle seated. The trigger opens nothing until all pass. Pulling the nozzle out of the tank shuts the valve. | `PumpController.SqueezeTrigger`, `UnseatNozzle` | `Trigger_does_nothing_until_grade_lift_and_seat_are_all_true`, `Pulling_the_nozzle_out_of_the_tank_stops_the_flow` |
| 21 | A driver walks away from an armed pump | 120 s with no pour cancels and reverses. 30 s notice, then the screen clears, but not while the bank still owes an answer. | `SaleStateMachine`, `SaleTimeouts` | `No_pour_for_two_minutes_cancels_and_reverses_the_hold`, `Finished_screen_clears_to_idle_after_thirty_seconds` |
| 22 | Fault mid-sale leaves the hold on, or gives the fuel away | A fault after fuel moved settles for the metered amount. A fault before any fuel reverses. | `SaleStateMachine.Apply` (Fault) | `Fault_while_fueling_closes_the_valve_and_settles_the_fuel_already_poured`, `Spoofed_pulses_during_a_sale_fault_the_pump_close_the_valve_and_settle_the_fuel_poured` |
| 23 | Own the admin panel and raise the debit or credit ceiling to $5000 | The site ceiling is a business limit upstream of the pump. The pump's hard limits clamp whatever arrives, and the clamp is in the ledger. | `SiteLimits`, `PumpAuthority.TryArm` | `Admin_raises_the_debit_ceiling_to_5000_and_the_pump_still_clamps_at_its_hard_limit` |
| 24 | Guess the admin PIN | Three wrong PINs lock the panel for five minutes. Every attempt, good or bad, is a ledger entry (`admin.refused`, `admin.locked`, `admin.limits`). | `SiteLimits.TrySet` | `Admin_panel_refuses_a_wrong_pin_and_locks_after_three`, `Brute_forcing_the_admin_pin_locks_the_panel_and_changes_nothing` |
| 25 | Pay debit with a card that has no PIN, to dodge the PIN check | The debit path presents the card as debit; the bank declines on PIN. | `PaymentGateway.PreAuthorizeAsync` | `Debit_with_a_card_that_has_no_pin_is_a_pin_decline_at_the_bank` |
| 26 | Lower the site ceiling while a hold is in flight, hoping the token keeps the old higher amount | The ceiling is recomputed as the lesser of the bank's answer and the site ceiling after the call returns. | `PaymentGateway.PreAuthorizeAsync` | `Admin_can_lower_the_debit_ceiling_and_the_next_sale_uses_it` |

## What the simulator does not defend, on purpose

1. **Card data.** There is none. A real reader encrypts under a DUKPT key inside a tamper boundary and the terminal never sees digits. That is PCI PTS and SRED territory and out of scope.
2. **Key management.** One shared HMAC key between the payment host and the pump. A real site injects per-device keys and uses a counter so the pump can refuse out-of-order commands.
3. **Physical attacks on the meter.** The pulse sanity check catches a pulser that lies upward. A meter that under-reports gives fuel away and only the totalizer against the tank dip would show it.
4. **Denial of service.** An attacker who can reach the pump mailbox can fill it with junk. Mailboxes are bounded, so the attacker waits, but the driver waits too.

## How to read the tests

Each control has at least one test that fails when the control is removed. The quickest way to see it: comment out the clamp in `PumpAuthority.TryArm`, run the suite, and watch attack 6 arm 600 gallons.
