import { CdkMenuModule } from '@angular/cdk/menu';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import { filter } from 'rxjs';
import { toSignal } from '@angular/core/rxjs-interop';
import { AuthStore } from '../core/auth/auth.store';
import { ConfigStore } from '../core/config/config.store';
import { LanguageService } from '../core/i18n/language.service';
import { ThemeService } from '../core/theme/theme.service';
import { RealtimeService } from '../core/realtime/realtime.service';
import { afterSignIn } from '../core/bootstrap';
import { Icon } from '../shared/ui/icon';
import { TermPipe, LocalNamePipe } from '../shared/pipes/pipes';
import { NAV, NavItem } from './nav';
import { ShellStatus } from './shell-status';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, TranslocoDirective, Icon, TermPipe, LocalNamePipe, CdkMenuModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell {
  protected readonly auth = inject(AuthStore);
  protected readonly config = inject(ConfigStore);
  protected readonly lang = inject(LanguageService);
  protected readonly theme = inject(ThemeService);
  protected readonly status = inject(ShellStatus);
  private readonly realtime = inject(RealtimeService);
  private readonly router = inject(Router);

  protected readonly navOpen = signal(false);
  protected readonly collapsed = signal(false);

  protected readonly sections = computed(() => {
    this.auth.permissions();
    this.config.features();
    return NAV.map((s) => ({ ...s, items: s.items.filter((i) => this.visible(i)) })).filter((s) => s.items.length > 0);
  });

  constructor() {
    const nav = toSignal(this.router.events.pipe(filter((e) => e instanceof NavigationEnd)));
    void nav;
    this.router.events.pipe(filter((e) => e instanceof NavigationEnd)).subscribe(() => this.navOpen.set(false));
  }

  protected isTerm(label: string): boolean { return label.startsWith('term:'); }
  protected termKey(label: string): string { return label.slice(5); }

  private visible(i: NavItem): boolean {
    const p = this.auth.me()?.profile;
    const linked = i.requiresLink === 'instructor' ? !!p?.instructorId : i.requiresLink === 'instructorOrGroup' ? !!(p?.instructorId || p?.studentGroupId) : true;
    return linked && (!i.permission || this.auth.has(i.permission)) && (!i.feature || this.config.feature(i.feature));
  }

  protected themeIcon(): string {
    const m = this.theme.mode();
    return m === 'light' ? 'sun' : m === 'dark' ? 'moon' : 'auto';
  }

  protected initials(): string {
    const n = this.auth.displayName();
    return n.split(/\s+/).filter((p) => p && !p.endsWith('.')).slice(0, 2).map((p) => p[0]).join('').toUpperCase() || '?';
  }

  protected async switchInstitution(id: string): Promise<void> {
    await this.auth.switchInstitution(id);
    await afterSignIn(this.auth, this.config, this.realtime);
    await this.router.navigateByUrl('/dashboard');
  }

  protected logout(): void {
    void this.realtime.disconnect();
    this.config.clear();
    void this.auth.logout();
  }
}
